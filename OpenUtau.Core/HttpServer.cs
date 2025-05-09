using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using OpenUtau.Core;
using OpenUtau.Core.Controllers;

namespace OpenUtau.Core {
    public class HttpServer {
        private IWebHost? host;
        private readonly int port;
        private readonly ManualResetEvent stopEvent = new ManualResetEvent(false);

        public HttpServer(int port = 5000) {
            this.port = port;
        }

        public void Start() {
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

            host.Start();
            Log.Information("HTTP server started");

            // 等待停止信号
            stopEvent.WaitOne();
        }

        public void Stop() {
            if (host != null) {
                Log.Information("Stopping HTTP server");
                host.StopAsync().Wait();
                host.Dispose();
                host = null;
                Log.Information("HTTP server stopped");
            }
            stopEvent.Set();
        }

        public override string ToString() {
            return $"HTTP Server running on port {port}";
        }
    }
}
