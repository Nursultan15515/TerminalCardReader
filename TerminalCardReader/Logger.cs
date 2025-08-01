using System;
using System.IO;

namespace TerminalCardReader
{
    public static class Logger
    {
        public static void WriteLog(string messageLog)
        {
            try
            {
                // Путь к папке рядом с .exe
                string exePath = AppDomain.CurrentDomain.BaseDirectory;
                string logDir = Path.Combine(exePath, "ActionLog");

                // Создаём папку, если нет
                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                // Имя файла по дате
                string fileName = DateTime.Now.ToString("yyyy-MM-dd") + ".txt";
                string filePath = Path.Combine(logDir, fileName);

                // Формат записи
                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {messageLog}";

                // Записываем в файл (append)
                File.AppendAllText(filePath, logEntry + Environment.NewLine);
            }
            catch (Exception ex)
            {
                // На всякий случай ловим ошибки логгера
                Console.WriteLine("Ошибка логирования: " + ex.Message);
            }
        }
    }
}
