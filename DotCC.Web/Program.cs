using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DotCC.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The sandbox compiles and runs entirely client-side (Compiler.EmitWat + the
// wabt/fd_write JS interop). Only the Python page fetches anything: libpython,
// lazily, and the standard library zip, both static files of this site.
#if DOTCC_PYTHON
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<Microsoft.AspNetCore.Components.WebAssembly.Services.LazyAssemblyLoader>();
builder.Services.AddScoped<DotCC.Web.Python.PythonRuntime>();
#endif

await builder.Build().RunAsync();
