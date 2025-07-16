using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace TerminalCardReader
{
    class Program
    {
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
            listener.Start();
            Console.WriteLine("🟢 Сервер запущен: http://localhost:8080/issue-card/");

            while (true)
            {
                HttpListenerContext context = listener.GetContext();
                ThreadPool.QueueUserWorkItem(o => HandleRequest(context));
            }
        }

        static void HandleRequest(HttpListenerContext context)
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

                var result = RunCardLogic();
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

            string crtPortName = "COM5";
            string rfidPortName = "COM4";

            if (File.Exists(crtPortFile))
                crtPortName = File.ReadAllText(crtPortFile).Trim();
            if (File.Exists(rfidPortFile))
                rfidPortName = File.ReadAllText(rfidPortFile).Trim();

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
                                if (parts.Length > 1)
                                    _rfidUid = parts[1];
                                else
                                    _rfidUid = data;
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
                    Console.WriteLine($"🔁 Попытка #{attempt}");

                    ExecuteFCCommand(2);

                    stopwatch = Stopwatch.StartNew();
                    int timeout = 7000;
                    int waited = 0;
                    while (!_rfidReceived && waited < timeout)
                    {
                        Thread.Sleep(200);
                        waited += 200;
                    }

                    if (_rfidReceived)
                    {
                        Console.WriteLine("✅ UID получен: " + _rfidUid);
                        ExecuteCommandWithEnq("DC");

                        Console.WriteLine("⏳ Ожидание 15 секунд, чтобы пользователь забрал карту...");
                        Thread.Sleep(15000);
                        ExecuteCommandWithEnq("CP");

                        success = true;
                        break;
                    }
                    else
                    {
                        Console.WriteLine("❌ UID не получен, возврат карты...");
                        ExecuteCommandWithEnq("CP");
                    }
                }
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    uid = (string)null,
                    error = ex.Message,
                    elapsedMilliseconds = _elapsedMilliseconds >= 0 ? _elapsedMilliseconds : null,
                    isCardEnd = isCardEnd,
                    attempts = attempt
                };
            }
            finally
            {
                if (_crtPort.IsOpen)
                    _crtPort.Close();
                if (_rfidPort.IsOpen)
                    _rfidPort.Close();
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
    }
}
