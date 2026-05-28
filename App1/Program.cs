using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "app1.session";
    })
    .AddOpenIdConnect(options =>
    {
        var kc = builder.Configuration.GetSection("Keycloak");
        options.Authority = kc["Authority"];
        options.ClientId = kc["ClientId"];
        options.ClientSecret = kc["ClientSecret"];
        options.ResponseType = "code";
        options.SaveTokens = true;
        options.GetClaimsFromUserInfoEndpoint = true;
        options.RequireHttpsMetadata = false;
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.SignedOutRedirectUri = "/";
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

var app2BaseUrl = builder.Configuration["App2BaseUrl"] ?? "http://localhost:5002";

app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html lang="pt-BR">
    <head>
      <meta charset="UTF-8">
      <title>App 1</title>
      <style>
        body { font-family: sans-serif; max-width: 600px; margin: 60px auto; padding: 20px; }
        h1 { color: #003d99; }
        .btn { display: inline-block; padding: 10px 24px; background: #003d99; color: #fff;
               border-radius: 6px; text-decoration: none; font-size: 1rem; }
        .badge { display: inline-block; background: #e8f0fe; color: #003d99;
                 padding: 4px 10px; border-radius: 4px; font-size: 0.85rem; }
      </style>
    </head>
    <body>
      <h1>Aplicação 1</h1>
      <p><span class="badge">Keycloak 1 — porta 8080</span></p>
      <p>Esta aplicação é autenticada de forma independente pelo <strong>Keycloak 1</strong>.</p>
      <a href="/dashboard" class="btn">Acessar Dashboard</a>
    </body>
    </html>
    """, "text/html"));

app.MapGet("/login", async (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true)
    {
        ctx.Response.Redirect("/dashboard");
        return;
    }
    await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/dashboard" });
}).AllowAnonymous();

app.MapGet("/dashboard", async (HttpContext ctx) =>
{
    if (!(ctx.User.Identity?.IsAuthenticated ?? false))
    {
        await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = "/dashboard" });
        return Results.Empty;
    }

    var username = ctx.User.FindFirst("preferred_username")?.Value
                   ?? ctx.User.Identity?.Name
                   ?? "Desconhecido";
    var email = ctx.User.FindFirst("email")?.Value ?? "-";
    var ssoLink = $"{app2BaseUrl}/dashboard?kc_idp_hint=kc1";

    return Results.Content($$"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
          <meta charset="UTF-8">
          <title>App 1 — Dashboard</title>
          <style>
            body { font-family: sans-serif; max-width: 700px; margin: 60px auto; padding: 20px; }
            h1 { color: #003d99; }
            .card { background: #f0f4ff; border-left: 4px solid #003d99;
                    padding: 16px 20px; border-radius: 6px; margin: 20px 0; }
            .card p { margin: 6px 0; }
            .label { font-weight: 600; color: #555; }
            .btn { display: inline-block; padding: 10px 24px; border-radius: 6px;
                   text-decoration: none; font-size: 1rem; margin: 6px 4px 0 0; }
            .btn-sso  { background: #006600; color: #fff; }
            .btn-logout { background: #cc2200; color: #fff; }
            .hint-box { background: #fffbe6; border: 1px solid #e6c300;
                        border-radius: 6px; padding: 12px 16px; margin-top: 24px;
                        font-size: 0.88rem; color: #555; }
            code { background: #eee; padding: 2px 6px; border-radius: 3px; }
          </style>
        </head>
        <body>
          <h1>App 1 — Dashboard</h1>
          <div class="card">
            <p><span class="label">Usuário:</span> {{username}}</p>
            <p><span class="label">E-mail:</span> {{email}}</p>
            <p><span class="label">Autenticado via:</span> Keycloak 1 (realm1)</p>
          </div>

          <h2>Navegar para a App 2 via SSO</h2>
          <p>
            O botão abaixo abre o <em>dashboard</em> da App 2. Como você já está autenticado aqui,
            o login na App 2 acontece automaticamente — sem digitar senha novamente.
          </p>
          <a href="{{ssoLink}}" class="btn btn-sso" target="_blank">Abrir App 2 via SSO ↗</a>
          <a href="/logout" class="btn btn-logout">Logout</a>

          <div class="hint-box">
            <strong>Como funciona:</strong> o link gerado é
            <code>{{ssoLink}}</code>.
            O App 2 detecta o <code>kc_idp_hint=kc1</code> e instrui o KC2 a federar a
            autenticação no KC1, que já possui sessão ativa neste browser.
          </div>
        </body>
        </html>
        """, "text/html");
}).AllowAnonymous();

app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    try
    {
        await ctx.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
            new AuthenticationProperties { RedirectUri = "/" });
    }
    catch
    {
        // KC indisponível: sessão local já foi limpa, redireciona para home.
        ctx.Response.Redirect("/");
    }
}).AllowAnonymous();

app.Run("http://localhost:5001");
