using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

namespace TerminalCardReader
{
    class Program
    {
        // Лок только для устройства (не для веба)
        static readonly SemaphoreSlim _deviceLock = new SemaphoreSlim(1, 1);

        // Текущее «ожидание подтверждения»
        static PendingOp _pending; // одновременно может быть максимум одна операция

        static SerialPort _crtPort;
        static SerialPort _rfidPort;

        static void Main(string[] args)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/issue-card/");       // фаза 1
            listener.Prefixes.Add("http://localhost:8080/confirm/"); // фаза 2
            listener.Prefixes.Add("http://localhost:8080/card-status/");
            listener.Start();
            Console.WriteLine("Сервер запущен: http://localhost:8080/");

            while (true)
            {
                var ctx = listener.GetContext();
                _ = Task.Run(() => RouteAsync(ctx));
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
                var path = req.Url.AbsolutePath;
                if ((path == "/issue-card" || path == "/issue-card/") && req.HttpMethod == "POST")
                {
                    await HandleIssueStartAsync(context);
                }
                else if ((path == "/confirm" || path == "/confirm/") && req.HttpMethod == "POST")
                {
                    await HandleIssueConfirmAsync(context);
                }
                else if (path == "/card-status/" && (req.HttpMethod == "GET" || req.HttpMethod == "POST"))
                {
                    await HandleStatusRequestAsync(context);
                }
                else
                {
                    res.StatusCode = 404;
                    await WriteTextAsync(res, "Not Found");
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

        // ---------- ФАЗА 1: читаем RFID, НО карту не плюём ----------
        static async Task HandleIssueStartAsync(HttpListenerContext context)
        {
            var res = context.Response;

            // Если уже есть незавершённая операция — ответим 409
            if (_pending != null && !_pending.Completed)
            {
                res.StatusCode = 409;
                await WriteJsonAsync(res, new { error = "Operation already pending", operationId = _pending.Id.ToString() });
                return;
            }

            await _deviceLock.WaitAsync(); // захватываем железо на всю сессию

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string crtPortName = ReadOrDefault(Path.Combine(baseDir, "crt_port.txt"), "COM5");
                string rfidPortName = ReadOrDefault(Path.Combine(baseDir, "rfid_port.txt"), "COM4");
                int initialWindowMs = ReadOrDefaultInt(Path.Combine(baseDir, "initial_window.txt"), 250);
                int idleGapMs = ReadOrDefaultInt(Path.Combine(baseDir, "idle_gap.txt"), 120);
                int confirmTimeout = ReadOrDefaultInt(Path.Combine(baseDir, "confirm_timeout.txt"), 30); // сек

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

                _crtPort.Open();
                _rfidPort.Open();

                Logger.WriteLog($"→ FC (подача карты), CRT={crtPortName}, RFID={rfidPortName}; win={initialWindowMs}/{idleGapMs} мс");
                ExecuteFCCommand(_crtPort, 2);

                var sw = Stopwatch.StartNew();

                // читаем burst ответа от RFID
                string frame = ReadBurst(_rfidPort, initialWindowMs, idleGapMs);
                frame = (frame ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

                string uid = null;
                if (!string.IsNullOrEmpty(frame) && frame.IndexOf("No Card", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    // Сносим HID[...] и берём ПОСЛЕДНЕЕ число
                    frame = Regex.Replace(frame, @"HID\[[^\]]*\]", "", RegexOptions.IgnoreCase).Trim();
                    var matches = Regex.Matches(frame, @"\b\d+\b");
                    if (matches.Count > 0)
                        uid = matches[matches.Count - 1].Value;
                }

                if (string.IsNullOrEmpty(uid))
                {
                    Logger.WriteLog("× UID не получен, делаем CP и освобождаем устройство");
                    ExecuteCommandWithEnq(_crtPort, "CP");
                    SafeClosePorts();
                    _deviceLock.Release();

                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new { success = false, uid = (string)null, error = "UID not received", elapsedMilliseconds = (long?)sw.ElapsedMilliseconds });
                    return;
                }

                Logger.WriteLog("✓ UID: " + uid);

                // Готовим pending-операцию: ждём confirm/deny
                var op = new PendingOp
                {
                    Id = Guid.NewGuid(),
                    Uid = uid,
                    Started = DateTime.UtcNow,
                    Cts = new CancellationTokenSource()
                };
                _pending = op;

                // Запускаем таймаут подтверждения
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(confirmTimeout), op.Cts.Token);
                        if (!op.Completed)
                        {
                            Logger.WriteLog("⏰ Таймаут подтверждения. Делаем CP и завершаем.");
                            ExecuteCommandWithEnq(_crtPort, "CP");
                            CompleteAndCleanup();
                        }
                    }
                    catch (TaskCanceledException) { /* ок, подтверждение пришло */ }
                });

                // Отдаём UID и operationId, карта пока внутри
                res.StatusCode = 200;
                await WriteJsonAsync(res, new
                {
                    success = true,
                    uid = uid,
                    operationId = op.Id.ToString(),
                    timeoutSec = confirmTimeout,
                    elapsedMilliseconds = (long?)sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                // Аварийно прибираем
                try { ExecuteCommandWithEnq(_crtPort, "CP"); } catch { }
                SafeClosePorts();
                _deviceLock.Release();
                _pending = null;

                res.StatusCode = 500;
                await WriteJsonAsync(res, new { success = false, error = ex.Message });
            }
        }

        // ---------- ФАЗА 2: подтверждение ----------
        static async Task HandleIssueConfirmAsync(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;

            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding))
                body = sr.ReadToEnd();

            string opId = null;
            bool allow = false;

            try
            {
                var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("operationId", out var idProp))
                    opId = idProp.GetString();
                if (json.RootElement.TryGetProperty("allow", out var allowProp))
                    allow = allowProp.GetBoolean();
            }
            catch
            {
                res.StatusCode = 400;
                await WriteJsonAsync(res, new { error = "Invalid JSON. Expected { operationId, allow }" });
                return;
            }

            if (_pending == null || _pending.Completed)
            {
                res.StatusCode = 410; // Gone
                await WriteJsonAsync(res, new { error = "No pending operation" });
                return;
            }
            if (!string.Equals(_pending.Id.ToString(), opId, StringComparison.OrdinalIgnoreCase))
            {
                res.StatusCode = 409;
                await WriteJsonAsync(res, new { error = "operationId mismatch" });
                return;
            }

            try
            {
                _pending.Cts.Cancel(); // останавливаем таймаут

                if (allow)
                {
                    Logger.WriteLog("✔ Подтверждение получено: плюём карту (DC).");
                    ExecuteCommandWithEnq(_crtPort, "DC");

                    // Если нужен авто-ретракт через 17 сек — оставляем. Иначе закомментируй.
                    Task.Run(() =>
                    {
                        try
                        {
                            Logger.WriteLog("⏳ Ждём 17 секунд, затем CP (если не забрали).");
                            Thread.Sleep(15000);
                            ExecuteCommandWithEnq(_crtPort, "CP");
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLog("! Ошибка при CP после DC: " + ex.Message);
                        }
                        finally
                        {
                            CompleteAndCleanup();
                        }
                    });

                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new { success = true, action = "dispensed", uid = _pending.Uid });
                }
                else
                {
                    Logger.WriteLog("✖ Отклонено сервером: делаем CP.");
                    ExecuteCommandWithEnq(_crtPort, "CP");
                    CompleteAndCleanup();

                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new { success = true, action = "returned" });
                }
            }
            catch (Exception ex)
            {
                // аварийно прибираем, чтобы не повиснуть
                try { ExecuteCommandWithEnq(_crtPort, "CP"); } catch { }
                CompleteAndCleanup();

                res.StatusCode = 500;
                await WriteJsonAsync(res, new { success = false, error = ex.Message });
            }
        }

        // ---------- Статус устройства (как у тебя было) ----------
        static async Task HandleStatusRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            await _deviceLock.WaitAsync();
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

                    var ack = new byte[1];
                    port.Read(ack, 0, 1);
                    if (ack[0] != 0x06) throw new Exception("Нет ACK");

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
                _deviceLock.Release();
            }
        }

        // ---------- Вспомогательные ----------
        static void CompleteAndCleanup()
        {
            try { SafeClosePorts(); } catch { }
            _pending = null;
            try { _deviceLock.Release(); } catch { }
        }

        static void SafeClosePorts()
        {
            try { if (_rfidPort != null && _rfidPort.IsOpen) _rfidPort.Close(); } catch { }
            try { if (_crtPort != null && _crtPort.IsOpen) _crtPort.Close(); } catch { }
        }

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

        static void ExecuteCommandWithEnq(SerialPort port, string cmd)
        {
            SendCommand(port, cmd);
            if (ReadAck(port))
            {
                Thread.Sleep(100);
                port.Write(new byte[] { 0x05 }, 0, 1); // ENQ
                Thread.Sleep(400);
            }
        }

        static void ExecuteFCCommand(SerialPort port, int position)
        {
            byte[] buffer = new byte[6];
            buffer[0] = 0x02;
            buffer[1] = (byte)'F';
            buffer[2] = (byte)'C';
            buffer[3] = (byte)(0x30 + position);
            buffer[4] = 0x03;
            buffer[5] = (byte)(buffer[0] ^ buffer[1] ^ buffer[2] ^ buffer[3] ^ buffer[4]);
            port.Write(buffer, 0, buffer.Length);
            if (ReadAck(port))
            {
                Thread.Sleep(100);
                port.Write(new byte[] { 0x05 }, 0, 1);
                Thread.Sleep(400);
            }
        }

        static void SendCommand(SerialPort port, string cmd)
        {
            byte[] buffer = new byte[5];
            buffer[0] = 0x02;
            buffer[1] = (byte)cmd[0];
            buffer[2] = (byte)cmd[1];
            buffer[3] = 0x03;
            buffer[4] = (byte)(buffer[0] ^ buffer[1] ^ buffer[2] ^ buffer[3]);
            port.Write(buffer, 0, buffer.Length);
        }

        static bool ReadAck(SerialPort port)
        {
            try
            {
                int b = port.ReadByte();
                return b == 0x06;
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
            var json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
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

    class PendingOp
    {
        public Guid Id;
        public string Uid;
        public DateTime Started;
        public bool Completed;
        public CancellationTokenSource Cts;
    }
}
