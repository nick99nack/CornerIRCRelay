using System;

namespace CornerIRCRelay
{
    public class Log
    {
        public static void Write(string message)
        {
            Console.WriteLine($"[{DateTime.Now.ToShortDateString()} {DateTime.Now.ToShortTimeString()}] {message}");
        }
    }
}