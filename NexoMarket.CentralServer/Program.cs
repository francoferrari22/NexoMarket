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
                Console.WriteLine("No se pudo iniciar NexoMarket Central Server en el puerto " + port + ".");
                Console.ReadKey();
                return;
            }
            Console.WriteLine("NexoMarket Central Server 4.0");
            string publicBase = Environment.GetEnvironmentVariable("PUBLIC_BASE_URL");
            Console.WriteLine("Marketplace: " + (string.IsNullOrWhiteSpace(publicBase) ? "http://localhost:" + port + "/" : publicBase.TrimEnd('/') + "/"));
            Console.WriteLine("API:         http://localhost:" + port + "/api");
            Console.WriteLine("Datos:       Data/nexomarket_stores.xml / Data/nexomarket_catalog.xml / Data/nexomarket_orders.xml");
        }
    }
}
