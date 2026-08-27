using System;

namespace NexoMarket.CentralServer
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            int port = 8095;
            string envPort = Environment.GetEnvironmentVariable("PORT");
            if (!string.IsNullOrWhiteSpace(envPort)) int.TryParse(envPort, out port);
            if (args.Length > 0) int.TryParse(args[0], out port);
            CentralServerService server = new CentralServerService(port);
            if (!server.Start())
            {
                Console.Error.WriteLine("[NexoMarket] ERROR: no se pudo iniciar el servidor en el puerto " + port + ".");
                Environment.Exit(1);
                return;
            }
            Console.WriteLine("[NexoMarket] Central Server 5.12.10 iniciado correctamente.");
            string publicBase = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
            Console.WriteLine("Marketplace: " + (string.IsNullOrWhiteSpace(publicBase) ? "http://localhost:" + port + "/" : publicBase.TrimEnd('/') + "/"));
            Console.WriteLine("API:         " + (string.IsNullOrWhiteSpace(publicBase) ? "http://localhost:" + port + "/api" : publicBase.TrimEnd('/') + "/api"));
            Console.WriteLine("Datos:       PostgreSQL central (XML/R2 solo como respaldo/migracion)");
            Console.WriteLine("DB status:   " + server.DatabaseStatusForLog());
            Console.WriteLine("[NexoMarket] Health: /health");
            Console.WriteLine("[NexoMarket] LIVE: esperando tráfico HTTP en 0.0.0.0:" + port);
            System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
        }
    }
}
