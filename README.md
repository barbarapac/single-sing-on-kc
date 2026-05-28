# Estudo — Single Sign-On entre dois Keycloaks distintos

Prova de conceito de SSO federado usando dois Keycloaks independentes e duas APIs .NET 10 Minimal API.

## Cenário

| | App 1 | App 2 |
|---|---|---|
| **URL** | http://localhost:5001 | http://localhost:5002 |
| **Keycloak** | KC1 — porta 8080 | KC2 — porta 8081 |
| **Realm** | realm1 | realm2 |
| **Usuário de teste** | user1 / password1 | user2 / password2 |

O usuário faz login no App 1 via KC1. Ao clicar em **"Abrir App 2 via SSO"**, o App 2 abre autenticado automaticamente — sem solicitar senha — mesmo sendo um Keycloak completamente separado.

Cada aplicação também suporta login independente direto (sem depender da outra).

## Arquitetura

```
Browser
  │
  ├─► App1 (5001) ──autenticado por──► KC1 (8080 / realm1)
  │         │
  │         │  clique no botão SSO
  │         │  gera link: http://localhost:5002/dashboard?kc_idp_hint=kc1
  │         ▼
  └─► App2 (5002) ──autenticado por──► KC2 (8081 / realm2)
                                            │
                              kc_idp_hint=kc1 ──► KC2 redireciona para KC1
                                            │         │
                                            │         └─► KC1 encontra sessão ativa
                                            │              └─► token emitido sem nova senha
                                            └─◄─ KC2 recebe token, cria sessão local
```

### Por que funciona: Identity Brokering

KC2 tem o KC1 registrado como Identity Provider (IdP) OIDC com alias `kc1`. O parâmetro `kc_idp_hint=kc1` instrui o KC2 a pular sua própria tela de login e delegar a autenticação diretamente ao KC1.

### Abordagem produção-ready

O App 1 **não conhece** internos do KC2 (client_id, realm, hostname). Ele apenas gera o link com `kc_idp_hint=kc1` apontando para a URL do App 2.

O App 2 captura o hint da query string, armazena em `AuthenticationProperties` e o evento `OnRedirectToIdentityProvider` o injeta no request OIDC para o KC2. O hint viaja serializado dentro do parâmetro `state` do fluxo OIDC.

## Pré-requisitos

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (mínimo 2 GB de memória alocada para Docker)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Como executar

### 1. Subir os Keycloaks

```bash
docker compose up -d
```

Aguarde 60–90 segundos até ambos estarem prontos. Verifique:

```bash
docker compose ps
```

Ambos devem estar com status `Up`. Para confirmar que o KC1 está pronto:
```
http://localhost:8080/realms/realm1/.well-known/openid-configuration
```

### 2. Rodar o App 1

```bash
cd App1
dotnet run
```

### 3. Rodar o App 2 (outro terminal)

```bash
cd App2
dotnet run
```

## Testando o fluxo SSO

1. Acesse **http://localhost:5001** → clique em "Acessar Dashboard"
2. Faça login com `user1 / password1` no KC1
3. No dashboard do App 1, clique em **"Abrir App 2 via SSO ↗"**
4. O App 2 deve abrir direto no dashboard, sem solicitar login
5. O dashboard do App 2 exibe o badge **"SSO via Keycloak 1 ✓"**

### Testando login independente no App 2

1. Acesse **http://localhost:5002** diretamente (sem passar pelo App 1)
2. Clique em "Acessar Dashboard" → faça login com `user2 / password2` no KC2
3. O dashboard exibe "Keycloak 2 (login local)"

## Estrutura do projeto

```
single-sing-on-kc/
├── docker-compose.yml
├── keycloak/
│   ├── kc1-realm.json        # realm1: app1-client + kc2-broker-client
│   └── kc2-realm.json        # realm2: app2-client + IdP kc1 apontando para KC1
├── App1/
│   ├── App1.csproj
│   ├── Program.cs
│   └── appsettings.json
└── App2/
    ├── App2.csproj
    ├── Program.cs
    └── appsettings.json
```

## Configurações relevantes

### Docker networking

| URL | Usado por | Motivo |
|---|---|---|
| `http://localhost:8080` | Browser / App1 | Acesso externo ao KC1 |
| `http://keycloak1:8080` | KC2 (backchannel) | Nome de serviço Docker para token exchange e JWKS |
| `http://localhost:8081` | Browser / App2 | Acesso externo ao KC2 |
| `http://localhost:8080` | `logoutUrl` no IdP | Logout é front-channel (browser), não backchannel |

KC1 usa `KC_HOSTNAME=localhost` + `KC_HOSTNAME_PORT=8080` para garantir que todos os tokens tenham `iss=http://localhost:8080/realms/realm1`, independente de qual hostname foi usado na requisição.

### Logout isolado por aplicação

O `logoutUrl` foi removido da configuração do IdP `kc1` no KC2. Isso significa que o logout no App 2 encerra apenas a sessão do KC2, sem propagar o logout para o KC1. Assim, o usuário que ainda está logado no App 1 pode continuar usando o SSO normalmente.

Se o `logoutUrl` estivesse presente, o logout no App 2 invalidaria a sessão KC1, fazendo com que o botão SSO do App 1 pedisse login novamente — comportamento indesejado quando o usuário quer sair apenas de uma das aplicações.

### PAR desabilitado no App 2

O .NET 9+/10 habilita Pushed Authorization Request (PAR) automaticamente quando o servidor anuncia suporte. Com PAR, os parâmetros de autorização (incluindo `kc_idp_hint`) são enviados via POST para o KC2 antes do redirect do browser. O KC2 26.0 não processa `kc_idp_hint` corretamente a partir de requisições PAR, então PAR foi desabilitado no App 2:

```csharp
options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
```

### Clientes Keycloak

**KC1 — realm1**

| Client | Usado por | Redirect URIs |
|---|---|---|
| `app1-client` | App 1 autenticar usuários | `http://localhost:5001/*` |
| `kc2-broker-client` | KC2 federar autenticação no KC1 | `http://localhost:8081/realms/realm2/broker/kc1/endpoint*` |

**KC2 — realm2**

| Client | Usado por | Redirect URIs |
|---|---|---|
| `app2-client` | App 2 autenticar usuários | `http://localhost:5002/*` |

## Reiniciar do zero

Para recriar os Keycloaks com configuração limpa (ex: após alterar os realm JSONs):

```bash
docker compose down -v
docker compose up -d
```

O `-v` remove os volumes, forçando a reimportação dos realms na próxima subida.
