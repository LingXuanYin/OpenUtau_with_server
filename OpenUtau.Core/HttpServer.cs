using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace OpenUtau.Core {
    public class HttpServer {
        private IWebHost? host;
        private readonly int port;

        public HttpServer(int port = 5000) {
            this.port = port;
        }

        public async Task StartAsync() {
            Log.Information($"Starting HTTP server on port {port}");
            
            host = new WebHostBuilder()
                .UseKestrel()
                .UseUrls($"http://127.0.0.1:{port}")
                .ConfigureServices(services => {
                    services.AddMvc();
                })
                .Configure(app => {
                    app.UseMvc();
                })
                .Build();

            await host.StartAsync();
            Log.Information("HTTP server started");
        }

        public async Task StopAsync() {
            if (host != null) {
                Log.Information("Stopping HTTP server");
                await host.StopAsync();
                host.Dispose();
                host = null;
                Log.Information("HTTP server stopped");
            }
        }
    }
} 
