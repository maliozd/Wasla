# Local HTTPS for Wasla tenant domains

## Why the SSL error happens

Wasla tenants use host-based URLs such as:

- `https://bahce-tadi.wasla.local:7200`
- `https://pilavcirahim.wasla.local:7200`
- `https://sushim.wasla.local:7200`

The default ASP.NET Core development certificate is issued for `localhost`, not for `*.wasla.local`. Browsers and clients such as **Wasla Print Bridge** therefore reject the connection with errors like:

> NET::ERR_CERT_COMMON_NAME_INVALID

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
   for `*.wasla.local`, `wasla.local`, `localhost`, and `127.0.0.1`
4. Converts to `.certs/wasla-local.pfx` (password: `wasla-dev`)

### Manual mkcert commands

```powershell
mkcert -install
mkcert -cert-file .certs/wasla-local.pem -key-file .certs/wasla-local-key.pem "*.wasla.local" wasla.local localhost 127.0.0.1
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

## 3. Configure OrderHub.Web (Development)

`src/OrderHub.Web/appsettings.Development.json` includes Kestrel certificate settings:

```json
"Kestrel": {
  "Certificates": {
    "Default": {
      "Path": "../../.certs/wasla-local.pfx",
      "Password": "wasla-dev"
    }
  }
}
```

The path is relative to the **Web project content root** (`src/OrderHub.Web/`), which resolves to the repo-root `.certs/` folder when you run:

```powershell
dotnet run --project src\OrderHub.Web\OrderHub.Web.csproj
```

If you publish or run from a different output directory, copy the PFX or adjust the path locally (do not commit machine-specific absolute paths).

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
127.0.0.1 bahce-tadi.wasla.local
127.0.0.1 pilavcirahim.wasla.local
127.0.0.1 sushim.wasla.local
127.0.0.1 wasla.local
```

### Another PC on the LAN (optional)

On the **client PC**, map the tenant hostname to the **Web host machine LAN IP**:

```
192.168.1.50 bahce-tadi.wasla.local
```

On that client PC you must also trust the mkcert root CA (run `mkcert -install` there after copying/exporting the CA), or HTTPS will still fail.

---

## 5. Run and test Web

```powershell
dotnet run --project src\OrderHub.Web\OrderHub.Web.csproj
```

Restart Web if it was already running so Kestrel reloads `.certs/wasla-local.pfx`.

Open:

- `https://wasla.local:7200`
- `https://localhost:7200`
- `https://bahce-tadi.wasla.local:7200/auth/login`

### Testing checklist

- [ ] Run `.\scripts\dev\setup-local-https.ps1`
- [ ] Hosts file entries added
- [ ] Run Web
- [ ] Open `https://wasla.local:7200` — no certificate warning
- [ ] Open `https://bahce-tadi.wasla.local:7200/auth/login` — no certificate warning
- [ ] Login succeeds and dashboard opens
- [ ] Print Bridge **Test connection** succeeds (no SSL error)

---

## 6. Print Bridge over local HTTPS

In the Print Bridge tray app **Settings**, set:

```
BaseUrl: https://bahce-tadi.wasla.local:7200
```

Requirements:

- Windows trusts the mkcert root CA (`mkcert -install` on that PC)
- Kestrel uses the generated PFX covering `*.wasla.local`
- Hosts file resolves the tenant domain to the Web host

Print Bridge does **not** ignore SSL errors by default. Fix trust at the OS level with mkcert.

---

## HTTP fallback (Development only)

For quick tests without HTTPS, you can still use:

```
http://bahce-tadi.wasla.local:5200
```

Development auth cookies support HTTP (`CookieSecurePolicy.SameAsRequest`). Print Bridge can use the same HTTP BaseUrl locally. HTTPS is recommended when testing Print Bridge SSL behavior before production.

---

## Troubleshooting

| Symptom | Likely cause |
|--------|----------------|
| Browser cert warning (`NET::ERR_CERT_COMMON_NAME_INVALID`) | Still using legacy `orderhub-local.pfx`, `mkcert -install` not run, wrong PFX path, or Web not restarted after regenerating cert |
| Print Bridge SSL error | mkcert CA not trusted, wrong BaseUrl, or hosts entry missing |
| Kestrel cannot load PFX | Script not run, or path wrong when using custom output folder |
| Login loop on HTTP | See auth cookie Development settings in `Program.cs` |

Regenerate certificates by re-running `.\scripts\dev\setup-local-https.ps1`.
