using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TerminalCardReader
{
    class Program
    {
        // Один общий лок, чтобы не пускать параллельные /issue-card и /card-status
        static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        static SerialPort _crtPort;
        static SerialPort _rfidPort;
        static string _rfidUid = null;
        static long? _elapsedMilliseconds = -1;
        static Stopwatch stopwatch;

        static void Main(string[] args)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/issue-card/");
            listener.Prefixes.Add("http://localhost:8080/card-status/");
            listener.Start();
            Console.WriteLine("Сервер запущен: http://localhost:8080/issue-card/");

            while (true)
            {
                var context = listener.GetContext();
                _ = Task.Run(() => RouteAsync(context));
            }
        }

        static async Task RouteAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            // CORS
            res.AddHeader("Access-Control-Allow-Origin", "*");
            res.AddHeader("Access-Control-Allow-Headers", "*");
            res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            if (req.HttpMethod == "OPTIONS") { res.StatusCode = 200; res.Close(); return; }

            try
            {
                if (req.Url.AbsolutePath == "/issue-card/")
                {
                    if (req.HttpMethod != "POST") { res.StatusCode = 405; await WriteTextAsync(res, "Method Not Allowed"); return; }
                    await HandleRequestAsync(context);
                }
                else if (req.Url.AbsolutePath == "/card-status/")
                {
                    if (req.HttpMethod != "GET" && req.HttpMethod != "POST") { res.StatusCode = 405; await WriteTextAsync(res, "Method Not Allowed"); return; }
                    await HandleStatusRequestAsync(context);
                }
                else
                {
                    res.StatusCode = 404; await WriteTextAsync(res, "Not Found");
                }
            }
            catch (Exception ex)
            {
                try { res.StatusCode = 500; await WriteJsonAsync(res, new { error = ex.Message }); } catch { }
            }
            finally
            {
                try { res.OutputStream.Close(); } catch { }
            }
        }

        static async Task HandleRequestAsync(HttpListenerContext context)
        {
            await _semaphore.WaitAsync(); // блокируем до полного завершения сценария
            var response = context.Response;

            try
            {
                var result = RunCardLogic(); // формируем результат быстро (но лок держим до CP)
                await WriteJsonAsync(response, result);
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonAsync(response, new { success = false, uid = (string)null, error = ex.Message, elapsedMilliseconds = _elapsedMilliseconds });
            }
            // НЕ освобождаем семафор здесь — он отпускается внутри фоновой задачи после CP/закрытия портов
        }

        static object RunCardLogic()
        {
            _rfidUid = null;
            _elapsedMilliseconds = -1;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string crtPortName = ReadOrDefault(Path.Combine(baseDir, "crt_port.txt"), "COM5");
            string rfidPortName = ReadOrDefault(Path.Combine(baseDir, "rfid_port.txt"), "COM4");
            int initialWindowMs = ReadOrDefaultInt(Path.Combine(baseDir, "initial_window.txt"), 250);
            int idleGapMs = ReadOrDefaultInt(Path.Combine(baseDir, "idle_gap.txt"), 120);

            _crtPort = new SerialPort(crtPortName, 9600, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };

            _rfidPort = new SerialPort(rfidPortName, 9600, Parity.None, 8, StopBits.One)
            {
                Encoding = Encoding.ASCII,
                ReadTimeout = 80
            };

            bool success = false;

            try
            {
                _crtPort.Open();
                _rfidPort.Open();

                Logger.WriteLog($"→ FC (подача карты), COM CRT={crtPortName}, RFID={rfidPortName}; win={initialWindowMs}/{idleGapMs} мс");
                ExecuteFCCommand(2);

                stopwatch = Stopwatch.StartNew();

                // Burst-чтение ответа от считывателя
                string frame = ReadBurst(_rfidPort, initialWindowMs, idleGapMs);
                frame = (frame ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

                if (!string.IsNullOrEmpty(frame) && frame.IndexOf("No Card", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // выбрасываем HID[...] и берём последнее число
                    frame = Regex.Replace(frame, @"HID\[[^\]]*\]", "", RegexOptions.IgnoreCase).Trim();
                    var matches = Regex.Matches(frame, @"\b\d+\b");
                    if (matches.Count > 0)
                    {
                        _rfidUid = matches[matches.Count - 1].Value;
                        _elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                        success = true;
                    }
                }

                if (success)
                {
                    Logger.WriteLog("✓ UID: " + _rfidUid);
                    ExecuteCommandWithEnq("DC");

                    // Возврат карты через 17 секунд, потом освобождаем лок
                    Task.Run(() =>
                    {
                        try
                        {
                            Logger.WriteLog("⏳ Ждём 17 секунд перед CP...");
                            Thread.Sleep(17000);
                            ExecuteCommandWithEnq("CP");
                            Logger.WriteLog("✓ Карта возвращена (CP).");
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLog("! Ошибка при возврате карты: " + ex.Message);
                        }
                        finally
                        {
                            try { if (_crtPort?.IsOpen == true) _crtPort.Close(); } catch { }
                            try { if (_rfidPort?.IsOpen == true) _rfidPort.Close(); } catch { }
                            _semaphore.Release();
                        }
                    });
                }
                else
                {
                    Logger.WriteLog("× UID не получен, выполняем CP и отпускаем лок");
                    ExecuteCommandWithEnq("CP");
                    try { if (_crtPort?.IsOpen == true) _crtPort.Close(); } catch { }
                    try { if (_rfidPort?.IsOpen == true) _rfidPort.Close(); } catch { }
                    _semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                // аварийно закрываем и освобождаем лок
                try { if (_crtPort?.IsOpen == true) _crtPort.Close(); } catch { }
                try { if (_rfidPort?.IsOpen == true) _rfidPort.Close(); } catch { }
                _semaphore.Release();

                return new
                {
                    success = false,
                    uid = (string)null,
                    error = ex.Message,
                    elapsedMilliseconds = _elapsedMilliseconds >= 0 ? _elapsedMilliseconds : null
                };
            }

            return new
            {
                success = success,
                uid = success ? _rfidUid : null,
                error = success ? null : "UID not received",
                elapsedMilliseconds = _elapsedMilliseconds >= 0 ? _elapsedMilliseconds : null
            };
        }

        static async Task HandleStatusRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            await _semaphore.WaitAsync();
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string crtPortName = ReadOrDefault(Path.Combine(baseDir, "crt_port.txt"), "COM5");

                int status;
                using (var port = new SerialPort(crtPortName, 9600, Parity.None, 8, StopBits.One))
                {
                    port.ReadTimeout = 1000;
                    port.WriteTimeout = 1000;
                    port.Open();

                    byte[] ap = new byte[] { 0x02, (byte)'A', (byte)'P', 0x03, (byte)(0x02 ^ (byte)'A' ^ (byte)'P' ^ 0x03) };
                    port.Write(ap, 0, ap.Length);

                    // ACK
                    var ack = new byte[1];
                    port.Read(ack, 0, 1);
                    if (ack[0] != 0x06) throw new Exception("Нет ACK");

                    // ENQ → ждём ответ
                    Thread.Sleep(50);
                    port.Write(new byte[] { 0x05 }, 0, 1);
                    Thread.Sleep(100);

                    byte[] buffer = new byte[12];
                    int read = port.Read(buffer, 0, buffer.Length);
                    if (read < 7 || buffer[0] != 0x02 || buffer[1] != (byte)'S' || buffer[2] != (byte)'F')
                        throw new Exception("Неверный формат ответа");

                    byte b5 = buffer[5];
                    byte b6 = buffer[6];
                    bool isPreEmpty = (b5 & 0x01) != 0;
                    bool isEmpty = (b6 & 0x08) != 0;

                    status = isEmpty ? 0 : (isPreEmpty ? 1 : 2);
                }

                await WriteJsonAsync(response, new { status });
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                await WriteJsonAsync(response, new { status = -1, error = ex.Message });
            }
            finally
            {
                _semaphore.Release();
            }
        }

        // ======== НИЖЕ — утилиты ========

        static string ReadBurst(SerialPort port, int initialWindowMs, int idleGapMs)
        {
            var sb = new StringBuilder(128);

            // ждём первый кусок
            while (true)
            {
                try
                {
                    var chunk = port.ReadExisting();
                    if (!string.IsNullOrEmpty(chunk)) { sb.Append(chunk); break; }
                    Thread.Sleep(5);
                }
                catch (TimeoutException) { }
            }

            int start = Environment.TickCount;
            int lastData = Environment.TickCount;

            while (true)
            {
                try
                {
                    var chunk = port.ReadExisting();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        sb.Append(chunk);
                        lastData = Environment.TickCount;
                    }
                    else
                    {
                        if (Environment.TickCount - lastData >= idleGapMs) break;
                    }
                }
                catch (TimeoutException)
                {
                    if (Environment.TickCount - lastData >= idleGapMs) break;
                }

                if ((Environment.TickCount - start) >= initialWindowMs &&
                    (Environment.TickCount - lastData) >= idleGapMs)
                    break;

                Thread.Sleep(5);
            }

            return sb.ToString();
        }

        static void ExecuteCommandWithEnq(string cmd)
        {
            SendCommand(cmd);
            if (ReadAck())
            {
                Thread.Sleep(100);
                _crtPort.Write(new byte[] { 0x05 }, 0, 1); // ENQ
                Thread.Sleep(400);
            }
        }

        static void ExecuteFCCommand(int position)
        {
            byte[] buffer = new byte[6];
            buffer[0] = 0x02;
            buffer[1] = (byte)'F';
            buffer[2] = (byte)'C';
            buffer[3] = (byte)(0x30 + position);
            buffer[4] = 0x03;
            buffer[5] = (byte)(buffer[0] ^ buffer[1] ^ buffer[2] ^ buffer[3] ^ buffer[4]);
            _crtPort.Write(buffer, 0, buffer.Length);
            if (ReadAck())
            {
                Thread.Sleep(100);
                _crtPort.Write(new byte[] { 0x05 }, 0, 1);
                Thread.Sleep(400);
            }
        }

        static void SendCommand(string cmd)
        {
            byte[] buffer = new byte[5];
            buffer[0] = 0x02;
            buffer[1] = (byte)cmd[0];
            buffer[2] = (byte)cmd[1];
            buffer[3] = 0x03;
            buffer[4] = (byte)(buffer[0] ^ buffer[1] ^ buffer[2] ^ buffer[3]);
            _crtPort.Write(buffer, 0, buffer.Length);
        }

        static bool ReadAck()
        {
            try
            {
                byte[] response = new byte[1];
                int bytesRead = _crtPort.Read(response, 0, 1);
                return bytesRead > 0 && response[0] == 0x06;
            }
            catch { return false; }
        }

        static async Task WriteTextAsync(HttpListenerResponse res, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            res.ContentType = "text/plain; charset=utf-8";
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        static async Task WriteJsonAsync(HttpListenerResponse res, object obj)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var bytes = Encoding.UTF8.GetBytes(json);
            res.ContentType = "application/json; charset=utf-8";
            await res.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        }

        static string ReadOrDefault(string path, string def)
        {
            try { return File.Exists(path) ? File.ReadAllText(path).Trim() : def; }
            catch { return def; }
        }
        static int ReadOrDefaultInt(string path, int def)
        {
            try { return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var v) ? v : def; }
            catch { return def; }
        }
    }
}
