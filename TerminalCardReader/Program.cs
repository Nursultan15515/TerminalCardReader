using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using PCSC;
using System.Linq;

namespace TerminalCardReader
{
    class Program
    {
        // Лок на «железо»
        static readonly SemaphoreSlim _deviceLock = new SemaphoreSlim(1, 1);

        // Незавершённая операция (ожидаем подтверждение)
        static PendingOp _pending;

        static SerialPort _crtPort;

        static void Main(string[] args)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/issue-card/");
            listener.Prefixes.Add("http://localhost:8080/confirm/");
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

        // ---------- ФАЗА 1: подать карту, считать через PC/SC, НЕ выдавать ----------
        static async Task HandleIssueStartAsync(HttpListenerContext context)
        {
            var res = context.Response;

            if (_pending != null && !_pending.Completed)
            {
                res.StatusCode = 409;
                await WriteJsonAsync(res, new { error = "Operation already pending", operationId = _pending.Id.ToString() });
                return;
            }

            await _deviceLock.WaitAsync();

            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string crtPortName = ReadOrDefault(Path.Combine(baseDir, "crt_port.txt"), "COM1");
                int confirmTimeout = ReadOrDefaultInt(Path.Combine(baseDir, "confirm_timeout.txt"), 30);
                string readerHint = ReadOrDefault(Path.Combine(baseDir, "rfid_reader.txt"), ""); // подсказка для выбора ридера

                _crtPort = new SerialPort(crtPortName, 9600, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 1500,
                    WriteTimeout = 1500
                };
                _crtPort.Open();

                Logger.WriteLog($"→ FC (подача карты в позицию чтения), CRT={crtPortName}");
                ExecuteFCCommand(_crtPort, 2); // подводим карту к антенне
                var sw = Stopwatch.StartNew();

                // читаем через PC/SC (до 6 сек) и декодируем HID 26-bit → card number
                var read = TryReadHidCardNumberViaPcsc(timeoutMs: 6000, readerHint: readerHint);

                if (read == null || string.IsNullOrEmpty(read.UidHex) || read.CardNumber == null)
                {
                    Logger.WriteLog("× UID/номер карты не получен, делаем CP и освобождаем устройство");
                    ExecuteCommandWithEnq(_crtPort, "CP");
                    SafeClosePorts();
                    _deviceLock.Release();

                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new
                    {
                        success = false,
                        uidHex = read?.UidHex,
                        card = (int?)null,
                        error = "RFID not read",
                        elapsedMilliseconds = (long?)sw.ElapsedMilliseconds
                    });
                    return;
                }

                Logger.WriteLog($"✓ UID: {read.UidHex}; HID FC={read.Facility}; Card={read.CardNumber}; Reader={read.ReaderName}");

                // Готовим pending-операцию: ждём confirm/deny
                var op = new PendingOp
                {
                    Id = Guid.NewGuid(),
                    Uid = read.UidHex,
                    Started = DateTime.UtcNow,
                    Cts = new CancellationTokenSource()
                };
                _pending = op;

                // Таймаут подтверждения
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
                    catch (TaskCanceledException) { }
                });

                // Отдаём данные клиенту (карта ещё внутри)
                res.StatusCode = 200;
                await WriteJsonAsync(res, new
                {
                    success = true,
                    uidHex = read.UidHex,
                    facility = read.Facility,
                    card = read.CardNumber,
                    reader = read.ReaderName,
                    operationId = op.Id.ToString(),
                    timeoutSec = confirmTimeout,
                    elapsedMilliseconds = (long?)sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
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
                res.StatusCode = 410;
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
                _pending.Cts.Cancel(); // остановить таймер

                if (allow)
                {
                    Logger.WriteLog("✔ Подтверждение: выдаём карту (DC).");
                    ExecuteCommandWithEnq(_crtPort, "DC");

                    // Авто-ретракт через 15 сек
                    Task.Run(() =>
                    {
                        try
                        {
                            Logger.WriteLog("⏳ Через 15 сек делаем CP (если не забрали).");
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
                try { ExecuteCommandWithEnq(_crtPort, "CP"); } catch { }
                CompleteAndCleanup();

                res.StatusCode = 500;
                await WriteJsonAsync(res, new { success = false, error = ex.Message });
            }
        }

        // ---------- Статус устройства ----------
        static async Task HandleStatusRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            await _deviceLock.WaitAsync();
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string crtPortName = ReadOrDefault(Path.Combine(baseDir, "crt_port.txt"), "COM1");

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
            try { if (_crtPort != null && _crtPort.IsOpen) _crtPort.Close(); } catch { }
        }

        static void ExecuteCommandWithEnq(SerialPort port, string cmd)
        {
            SendCommand(port, cmd);
            if (ReadAck(port))
            {
                Thread.Sleep(100);
                port.Write(new byte[] { 0x05 }, 0, 1); // ENQ
                Thread.Sleep(400);
                try { while (port.BytesToRead > 0) port.ReadExisting(); } catch { }
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
                try { while (port.BytesToRead > 0) port.ReadExisting(); } catch { }
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

        // ======== PC/SC чтение + HID Wiegand-26 декодирование ========
        class RfidReadResult
        {
            public string ReaderName;
            public string UidHex;
            public int? Facility;
            public int? CardNumber; // ← это твой «64410»
        }

        static RfidReadResult TryReadHidCardNumberViaPcsc(int timeoutMs, string readerHint = null)
        {
            var result = new RfidReadResult();
            using (var ctx = ContextFactory.Instance.Establish(SCardScope.System))
            {
                var readers = ctx.GetReaders();
                if (readers == null || readers.Length == 0) return null;

                var ordered = readers
                    .OrderByDescending(r =>
                        (!string.IsNullOrWhiteSpace(readerHint) && r.IndexOf(readerHint, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        r.IndexOf("omnikey", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.IndexOf("contactless", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.IndexOf(" nfc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.IndexOf(" rfid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.IndexOf(" cl", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();

                var sw = Stopwatch.StartNew();
                foreach (var name in ordered)
                {
                    using (var reader = new SCardReader(ctx))
                    {
                        while (sw.ElapsedMilliseconds < timeoutMs)
                        {
                            var rc = reader.Connect(name, SCardShareMode.Shared, SCardProtocol.Any);
                            if (rc == SCardError.Success)
                            {
                                var sendPci = SCardPCI.GetPci(reader.ActiveProtocol);

                                foreach (var apdu in new[] {
                                    new byte[]{0xFF,0xCA,0x00,0x00,0x00},
                                    new byte[]{0xFF,0xCA,0x01,0x00,0x00}
                                })
                                {
                                    byte[] recv = new byte[256];
                                    rc = reader.Transmit(sendPci, apdu, ref recv);
                                    if (rc == SCardError.Success && recv.Length >= 2)
                                    {
                                        byte sw1 = recv[recv.Length - 2], sw2 = recv[recv.Length - 1];
                                        if (sw1 == 0x90 && sw2 == 0x00)
                                        {
                                            int len = recv.Length - 2;
                                            var uid = new byte[len];
                                            Array.Copy(recv, uid, len);

                                            result.ReaderName = name;
                                            result.UidHex = BitConverter.ToString(uid).Replace("-", "");

                                            // HID 26-bit (H10301): первые 4 байта как BE + сдвиг 7 бит
                                            if (uid.Length >= 4)
                                            {
                                                uint be = ((uint)uid[0] << 24) | ((uint)uid[1] << 16) | ((uint)uid[2] << 8) | uid[3];
                                                uint core26 = (be >> 7) & 0x03FFFFFF;
                                                int facility = (int)((core26 >> 16) & 0xFF);
                                                int card = (int)(core26 & 0xFFFF);

                                                result.Facility = facility;
                                                result.CardNumber = card; // ← «64410»
                                            }
                                            return result;
                                        }
                                    }
                                }
                            }
                            else if (rc != SCardError.NoSmartcard && rc != SCardError.RemovedCard && rc != SCardError.NotReady)
                            {
                                // другая ошибка — к следующему ридеру
                                break;
                            }

                            Thread.Sleep(120);
                        }
                    }
                }
            }
            return null;
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

    // Простой логгер в файл
    //static class Logger
    //{
    //    static readonly object _sync = new object();
    //    public static void WriteLog(string message)
    //    {
    //        try
    //        {
    //            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ActionLog");
    //            Directory.CreateDirectory(dir);
    //            var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
    //            lock (_sync)
    //            {
    //                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}\r\n", Encoding.UTF8);
    //            }
    //        }
    //        catch { /* ignore */ }
    //    }
    //}
}
