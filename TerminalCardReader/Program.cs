using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TerminalCardReader
{
    class Program
    {
        static SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        static SerialPort _crtPort;
        static SerialPort _rfidPort;
        static string _rfidUid = null;
        static bool _rfidReceived = false;
        static long? _elapsedMilliseconds = -1;
        static bool isCardEnd = false;
        static Stopwatch stopwatch;

        static void Main(string[] args)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:8080/issue-card/");
            listener.Prefixes.Add("http://localhost:8080/card-status/");
            listener.Start();
            Console.WriteLine("Сервер запущен: http://localhost:8080/issue-card/");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                _ = Task.Run(() =>
                {
                    if (context.Request.Url.AbsolutePath == "/issue-card/")
                        HandleRequestAsync(context);
                    else if (context.Request.Url.AbsolutePath == "/card-status/")
                        HandleStatusRequestAsync(context);
                    else
                        context.Response.StatusCode = 404;
                });
            }

        }

        static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Headers", "*");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

                if (context.Request.HttpMethod != "POST")
                {
                    response.StatusCode = 405;
                    byte[] msg = Encoding.UTF8.GetBytes("Method Not Allowed");
                    response.OutputStream.Write(msg, 0, msg.Length);
                    response.Close();
                    return;
                }

                await _semaphore.WaitAsync(); // Блокировка

                var result = RunCardLogic(); // JSON возвращается быстро

                string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                response.StatusCode = 500;
                string jsonError = JsonSerializer.Serialize(new { error = ex.Message });
                byte[] buffer = Encoding.UTF8.GetBytes(jsonError);
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            finally
            {
                response.OutputStream.Close();
                // ? Не освобождаем семафор здесь — только после возврата карты!
            }
        }

        static object RunCardLogic()
        {
            _rfidUid = null;
            _rfidReceived = false;
            _elapsedMilliseconds = -1;
            isCardEnd = false;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string crtPortFile = Path.Combine(baseDir, "crt_port.txt");
            string rfidPortFile = Path.Combine(baseDir, "rfid_port.txt");

            string crtPortName = File.Exists(crtPortFile) ? File.ReadAllText(crtPortFile).Trim() : "COM5";
            string rfidPortName = File.Exists(rfidPortFile) ? File.ReadAllText(rfidPortFile).Trim() : "COM4";

            _crtPort = new SerialPort(crtPortName, 9600, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 1000,
                WriteTimeout = 1000
            };

            _rfidPort = new SerialPort(rfidPortName, 9600, Parity.None, 8, StopBits.One);
            _rfidPort.DataReceived += (s, e) =>
            {
                try
                {
                    string data = _rfidPort.ReadExisting().Trim();
                    if (!string.IsNullOrWhiteSpace(data))
                    {
                        if (data == "No Card")
                        {
                            isCardEnd = true;
                        }
                        else if (!_rfidReceived)
                        {
                            if (data.StartsWith("HID["))
                            {
                                var parts = data.Split(' ');
                                _rfidUid = parts.Length > 1 ? parts[1] : data;
                            }
                            else
                            {
                                _rfidUid = data;
                            }

                            _rfidReceived = true;
                            stopwatch.Stop();
                            _elapsedMilliseconds = stopwatch.ElapsedMilliseconds;
                        }
                    }
                }
                catch { }
            };

            int maxAttempts = 3;
            int attempt = 0;
            bool success = false;

            try
            {
                _crtPort.Open();
                _rfidPort.Open();

                while (attempt < maxAttempts && !_rfidReceived)
                {
                    attempt++;
                    Logger.WriteLog($"?? Попытка #{attempt}");

                    ExecuteFCCommand(2);

                    stopwatch = Stopwatch.StartNew();
                    int timeout = 10000;
                    int waited = 0;
                    while (!_rfidReceived && waited < timeout)
                    {
                        Thread.Sleep(200);
                        waited += 200;
                    }

                    if (_rfidReceived)
                    {
                        Logger.WriteLog("? UID получен: " + _rfidUid);
                        ExecuteCommandWithEnq("DC");

                        // ? Возврат карты и Release после 15 сек
                        Task.Run(() =>
                        {
                            try
                            {
                                Logger.WriteLog("? Ожидание 17 секунд перед возвратом...");
                                Thread.Sleep(17000);
                                ExecuteCommandWithEnq("CP");
                                Logger.WriteLog("? Карта возвращена.");
                            }
                            catch (Exception ex)
                            {
                                Logger.WriteLog("? Ошибка возврата карты: " + ex.Message);
                            }
                            finally
                            {
                                if (_crtPort?.IsOpen == true) _crtPort.Close();
                                if (_rfidPort?.IsOpen == true) _rfidPort.Close();

                                // ? Семафор освобождаем ТОЛЬКО здесь
                                _semaphore.Release();
                            }
                        });

                        success = true;
                        break;
                    }
                    else
                    {
                        Logger.WriteLog("? UID не получен, возврат карты...");
                        ExecuteCommandWithEnq("CP");
                        _crtPort.Close();
                        _rfidPort.Close();
                        _semaphore.Release(); // ? Освобождаем сразу при неудаче
                    }
                }
            }
            catch (Exception ex)
            {
                _semaphore.Release(); // ?? При критической ошибке — тоже отпускаем
                return new
                {
                    success = false,
                    uid = (string)null,
                    error = ex.Message,
                    elapsedMilliseconds = _elapsedMilliseconds >= 0 ? _elapsedMilliseconds : null,
                    attempts = attempt
                };
            }

            return new
            {
                success = success,
                uid = success ? _rfidUid : null,
                error = success ? null : "UID not received after retries",
                elapsedMilliseconds = _elapsedMilliseconds >= 0 ? _elapsedMilliseconds : null,
                isCardEnd = isCardEnd,
                attempts = attempt
            };
        }

        static void ExecuteCommandWithEnq(string cmd)
        {
            SendCommand(cmd);
            if (ReadAck())
            {
                Thread.Sleep(100);
                _crtPort.Write(new byte[] { 0x05 }, 0, 1);
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

        static async Task HandleStatusRequestAsync(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Headers", "*");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string crtPortFile = Path.Combine(baseDir, "crt_port.txt");
                string crtPortName = File.Exists(crtPortFile) ? File.ReadAllText(crtPortFile).Trim() : "COM5";

                int status = 0;

                using (var port = new SerialPort(crtPortName, 9600, Parity.None, 8, StopBits.One))
                {
                    port.ReadTimeout = 1000;
                    port.WriteTimeout = 1000;
                    port.Open();

                    byte[] apCommand = new byte[]
                    {
                0x02, (byte)'A', (byte)'P', 0x03,
                (byte)(0x02 ^ (byte)'A' ^ (byte)'P' ^ 0x03)
                    };

                    port.Write(apCommand, 0, apCommand.Length);

                    byte[] ack = new byte[1];
                    port.Read(ack, 0, 1);
                    if (ack[0] != 0x06)
                        throw new Exception("Нет ACK");

                    Thread.Sleep(50);
                    port.Write(new byte[] { 0x05 }, 0, 1);

                    Thread.Sleep(100);
                    byte[] buffer = new byte[12];
                    int bytesRead = port.Read(buffer, 0, buffer.Length);
                    if (bytesRead < 7 || buffer[0] != 0x02 || buffer[1] != (byte)'S' || buffer[2] != (byte)'F')
                        throw new Exception("Неверный формат ответа");

                    byte byte5 = buffer[5];
                    byte byte6 = buffer[6];

                    bool isPreEmpty = (byte5 & 0x01) != 0; // Byte5, bit 0
                    bool isEmpty = (byte6 & 0x08) != 0;     // Byte6, bit 3

                    status = isEmpty ? 0 : isPreEmpty ? 1 : 2;
                }

                var json = JsonSerializer.Serialize(new { status = status });
                byte[] bufferOut = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                response.ContentLength64 = bufferOut.Length;
                await response.OutputStream.WriteAsync(bufferOut, 0, bufferOut.Length);
            }
            catch (Exception ex)
            {
                var errorJson = JsonSerializer.Serialize(new { status = -1, error = ex.Message });
                byte[] bufferOut = Encoding.UTF8.GetBytes(errorJson);
                response.ContentType = "application/json";
                response.StatusCode = 500;
                await response.OutputStream.WriteAsync(bufferOut, 0, bufferOut.Length);
            }
            finally
            {
                response.OutputStream.Close();
            }
        }
    }
}
