
using Microsoft.Owin;
using Owin;

namespace ReportWeb
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            app.MapSignalR();
        }
    }
}