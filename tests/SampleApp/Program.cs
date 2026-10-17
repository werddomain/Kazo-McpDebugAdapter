using System;
using System.Threading;

class Program
{
    static int Add(int a, int b)
    {
        var sum = a + b;
        return sum;
    }

    static void Main(string[] args)
    {
        var name = "Benoît";
        Console.WriteLine($"Bonjour {name} – démarrage");
        var loops = args.Length > 0 && args[0] == "long" ? 1000 : 5;
        for (int i = 0; i < loops; i++)
        {
            var r = Add(i, 10);
            Console.WriteLine($"r={r} é");
            Thread.Sleep(args.Length > 0 ? 500 : 50);
        }
        Console.WriteLine("Fin");
    }
}
