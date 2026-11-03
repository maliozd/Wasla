# Local HTTPS for Wasla tenant domains

## Why the SSL error happens

Wasla tenants use host-based URLs under the application domain ([architecture/tenancy.md](architecture/tenancy.md#host-contract-was-66)), such as:

- `https://bahce-tadi.app.wasla.local:7200`
- `https://pilavcirahim.app.wasla.local:7200`
- `https://sushim.app.wasla.local:7200`

The default ASP.NET Core development certificate is issued for `localhost`, not for these hosts. Browsers and clients such as **Wasla Print Bridge** therefore reject the connection with errors like:

> NET::ERR_CERT_COMMON_NAME_INVALID

Print Bridge logs the same failure as `RemoteCertificateNameMismatch`.

A wildcard name covers exactly one label: `*.wasla.local` covers `app.wasla.local` and `www.wasla.local`, but **not** `sushim.app.wasla.local`. Tenant hosts need `*.app.wasla.local`. A certificate generated before WAS-66 (only `*.wasla.local`) must be regenerated.

For local development, use a trusted certificate that covers your tenant domains. This repo provides a **mkcert**-based setup.

Production HTTPS behavior is unchanged. Do not disable HTTPS or ignore certificate errors in Production.

**Legacy:** Older local setups used `*.orderhub.local` and `.certs/orderhub-local.pfx`. That certificate does not cover `*.wasla.local`. Regenerate with the script below.

---

## Quick start

From the repository root:

```powershell
.\scripts\dev\setup-local-https.ps1
```

Then configure hosts, run Web, and test login.

---

## 1. Install mkcert (Windows)

### Preferred: winget

```powershell
winget install FiloSottile.mkcert
```

Open a **new** terminal after installation so `mkcert` is on `PATH`.

### Fallback: manual download

1. Download `mkcert-v*-windows-amd64.exe` from [mkcert releases](https://github.com/FiloSottile/mkcert/releases)
2. Rename it to `mkcert.exe`
3. Place it in a folder on your `PATH` (for example a personal `bin` folder)

---

## 2. Generate local certificates

Run the helper script from the repo root:

```powershell
.\scripts\dev\setup-local-https.ps1
```

The script:

1. Creates `.certs/` (gitignored)
2. Runs `mkcert -install` to trust the local root CA in Windows
3. Generates:
   - `.certs/wasla-local.pem`
   - `.certs/wasla-local-key.pem`
   for `*.app.wasla.local`, `app.wasla.local`, `*.wasla.local`, `wasla.local`, `localhost`, and `127.0.0.1`
4. Converts to `.certs/wasla-local.pfx` (password: `wasla-dev`)

### Manual mkcert commands

```powershell
mkcert -install
mkcert -cert-file .certs/wasla-local.pem -key-file .certs/wasla-local-key.pem "*.app.wasla.local" app.wasla.local "*.wasla.local" wasla.local localhost 127.0.0.1
```

### Manual PEM → PFX conversion

If OpenSSL is available (Git for Windows includes it):

```powershell
openssl pkcs12 -export `
  -out .certs/wasla-local.pfx `
  -inkey .certs/wasla-local-key.pem `
  -in .certs/wasla-local.pem `
  -password pass:wasla-dev
```

Git for Windows OpenSSL is often at:

`C:\Program Files\Git\usr\bin\openssl.exe`

Install Git for Windows if needed: [https://git-scm.com/download/win](https://git-scm.com/download/win)

**Do not commit** `.certs/` contents. Private keys and generated certificates stay local only.

Old `.certs/orderhub-local.*` files may remain on disk from earlier setups; they are unused and safe to delete locally after regenerating.

---

## 3. Configure Wasla.Web (Development)

The committed Web configuration names no HTTPS certificate. Point Kestrel at the PFX on your machine, for example with user environment variables (open a new terminal or restart Visual Studio afterwards):

```powershell
setx Kestrel__Certificates__Default__Path "<repo root>\.certs\wasla-local.pfx"
setx Kestrel__Certificates__Default__Password "wasla-dev"
```

Use an absolute path. Without these variables Kestrel serves the ASP.NET Core development certificate (`localhost` only). Do not commit machine-specific paths or passwords.

If these variables already point at another PFX (for example one kept outside the repository), regenerate that file with the same names instead, or point the variables at the new one.

`Properties/launchSettings.json` exposes:

- HTTP: `http://0.0.0.0:5200`
- HTTPS: `https://0.0.0.0:7200`

---

## 4. Hosts file

Map tenant domains to the machine that runs Wasla Web.

### Same PC (typical)

Edit as Administrator:

`C:\Windows\System32\drivers\etc\hosts`

```
127.0.0.1 wasla.local
127.0.0.1 app.wasla.local
127.0.0.1 bahce-tadi.app.wasla.local
127.0.0.1 sushim.app.wasla.local
```

There is no wildcard in the hosts file: add one line per tenant slug.

### Another PC on the LAN (optional)

On the **client PC**, map the tenant hostname to the **Web host machine LAN IP**:

```
192.168.1.50 bahce-tadi.app.wasla.local
```

On that client PC you must also trust the mkcert root CA (run `mkcert -install` there after copying/exporting the CA), or HTTPS will still fail.

---

## 5. Run and test Web

```powershell
dotnet run --project src\Wasla.Web\Wasla.Web.csproj
```

Restart Web if it was already running (in Visual Studio, stop and start debugging) so Kestrel reloads the PFX. Kestrel reads it only at startup.

Open:

- `https://wasla.local:7200`
- `https://localhost:7200`
- `https://bahce-tadi.app.wasla.local:7200/auth/login`

### Testing checklist

- [ ] Run `.\scripts\dev\setup-local-https.ps1`
- [ ] Hosts file entries added
- [ ] Kestrel certificate variables point at the generated PFX
- [ ] Run Web
- [ ] Open `https://wasla.local:7200` — no certificate warning
- [ ] Open `https://bahce-tadi.app.wasla.local:7200/auth/login` — no certificate warning
- [ ] Login succeeds and dashboard opens
- [ ] Print Bridge **Check connection** succeeds (no SSL error)

---

## 6. Print Bridge over local HTTPS

Connect Print Bridge with a setup link from the tenant's device page, or in the app's connection dialog enter the tenant address as the server URL (`ServerUrl`, see [integrations/print-bridge.md](integrations/print-bridge.md#setup-canonical-fields)):

```
https://bahce-tadi.app.wasla.local:7200
```

Requirements:

- Windows trusts the mkcert root CA (`mkcert -install` on that PC)
- Kestrel uses the generated PFX covering `*.app.wasla.local`
- Hosts file resolves the tenant domain to the Web host

Print Bridge does **not** ignore SSL errors by default. Fix trust at the OS level with mkcert.

---

## HTTP fallback (Development only)

For quick tests without HTTPS, you can still use:

```
http://bahce-tadi.app.wasla.local:5200
```

Development auth cookies support HTTP (`CookieSecurePolicy.SameAsRequest`). Print Bridge can use the same HTTP server URL locally. HTTPS is recommended when testing Print Bridge SSL behavior before production.

---

## Troubleshooting

| Symptom | Likely cause |
|--------|----------------|
| Browser cert warning (`NET::ERR_CERT_COMMON_NAME_INVALID`) | Still using legacy `orderhub-local.pfx`, `mkcert -install` not run, wrong PFX path, or Web not restarted after regenerating cert |
| Certificate warning or Print Bridge `RemoteCertificateNameMismatch` only on `{slug}.app.wasla.local` | Certificate generated before WAS-66 covers `*.wasla.local` but not `*.app.wasla.local`; regenerate and restart Web |
| Print Bridge SSL error | mkcert CA not trusted, wrong server URL, or hosts entry missing |
| Kestrel cannot load PFX | Script not run, or `Kestrel__Certificates__Default__Path` / `__Password` wrong |
| Which certificate is Web serving? | Check `Kestrel__Certificates__Default__Path` in the user and machine environment; Kestrel uses that file, not `.certs/` by itself |
| Login loop on HTTP | See auth cookie Development settings in `Program.cs` |

Regenerate certificates by re-running `.\scripts\dev\setup-local-https.ps1`.
