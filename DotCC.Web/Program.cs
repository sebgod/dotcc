using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using DotCC.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// No HttpClient / services needed: the sandbox compiles and runs entirely
// client-side (Compiler.EmitWat + the wabt/fd_write JS interop). Nothing is fetched.

await builder.Build().RunAsync();
