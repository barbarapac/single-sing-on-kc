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
        options.Cookie.Name = "app2.session";
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
        options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
        options.SignedOutRedirectUri = "/";

        options.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                // Tenta AuthenticationProperties primeiro; fallback: query string do request atual
                // (o evento dispara dentro do mesmo request do /dashboard, então a query ainda está acessível)
                context.Properties.Items.TryGetValue("kc_idp_hint", out var hint);
                if (string.IsNullOrEmpty(hint))
                    hint = context.HttpContext.Request.Query["kc_idp_hint"].FirstOrDefault();

                if (!string.IsNullOrEmpty(hint))
                {
                    context.ProtocolMessage.SetParameter("kc_idp_hint", hint);
                    Console.WriteLine($"[SSO] kc_idp_hint='{hint}' injetado na URL de autorização do KC2");
                }
                else
                {
                    Console.WriteLine("[SSO] kc_idp_hint ausente — login normal no KC2");
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Content("""
    <!DOCTYPE html>
    <html lang="pt-BR">
    <head>
      <meta charset="UTF-8">
      <title>App 2</title>
      <style>
        body { font-family: sans-serif; max-width: 600px; margin: 60px auto; padding: 20px; }
        h1 { color: #005500; }
        .btn { display: inline-block; padding: 10px 24px; background: #005500; color: #fff;
               border-radius: 6px; text-decoration: none; font-size: 1rem; }
        .badge { display: inline-block; background: #e8f5e9; color: #005500;
                 padding: 4px 10px; border-radius: 4px; font-size: 0.85rem; }
      </style>
    </head>
    <body>
      <h1>Aplicação 2</h1>
      <p><span class="badge">Keycloak 2 — porta 8081</span></p>
      <p>Esta aplicação é autenticada de forma independente pelo <strong>Keycloak 2</strong>.</p>
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
        var props = new AuthenticationProperties { RedirectUri = "/dashboard" };

        // Captura o hint da query string e armazena nas propriedades de autenticação.
        // O evento OnRedirectToIdentityProvider (acima) lê daqui e injeta no request OIDC.
        // Dessa forma, o App 2 não precisa conhecer a URL do KC2 nem do KC1.
        var hint = ctx.Request.Query["kc_idp_hint"].ToString();
        if (!string.IsNullOrEmpty(hint))
            props.Items["kc_idp_hint"] = hint;

        await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, props);
        return Results.Empty;
    }

    var username = ctx.User.FindFirst("preferred_username")?.Value
                   ?? ctx.User.Identity?.Name
                   ?? "Desconhecido";
    var email = ctx.User.FindFirst("email")?.Value ?? "-";

    // Keycloak inclui "identity_provider" nas claims quando o usuário autenticou via IdP externo.
    var idp = ctx.User.FindFirst("identity_provider")?.Value;
    var authSource = idp == "kc1"
        ? "<span class=\"sso-tag\">SSO via Keycloak 1 ✓</span>"
        : "Keycloak 2 (login local)";

    return Results.Content($$"""
        <!DOCTYPE html>
        <html lang="pt-BR">
        <head>
          <meta charset="UTF-8">
          <title>App 2 — Dashboard</title>
          <style>
            body { font-family: sans-serif; max-width: 700px; margin: 60px auto; padding: 20px; }
            h1 { color: #005500; }
            .card { background: #f0fff0; border-left: 4px solid #005500;
                    padding: 16px 20px; border-radius: 6px; margin: 20px 0; }
            .card p { margin: 6px 0; }
            .label { font-weight: 600; color: #555; }
            .sso-tag { background: #005500; color: #fff; padding: 3px 10px;
                       border-radius: 4px; font-size: 0.85rem; }
            .btn { display: inline-block; padding: 10px 24px; border-radius: 6px;
                   text-decoration: none; font-size: 1rem; }
            .btn-logout { background: #cc2200; color: #fff; }
          </style>
        </head>
        <body>
          <h1>App 2 — Dashboard</h1>
          <div class="card">
            <p><span class="label">Usuário:</span> {{username}}</p>
            <p><span class="label">E-mail:</span> {{email}}</p>
            <p><span class="label">Autenticado via:</span> {{authSource}}</p>
          </div>
          <a href="/logout" class="btn btn-logout">Logout</a>
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
        ctx.Response.Redirect("/");
    }
}).AllowAnonymous();

app.Run("http://localhost:5002");
