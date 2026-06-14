# Wasla â€“ Yerel GeliÅŸtirme (TR)

Bu dokÃ¼man, Wasla MVP demo akÄ±ÅŸÄ±nÄ± yerelde Ã§alÄ±ÅŸtÄ±rmak iÃ§indir.

## Ã–nkoÅŸullar

- .NET SDK (repo `net8.0` hedefli)
- SQL Server / LocalDB (varsayÄ±lan CentralDb baÄŸlantÄ±sÄ± LocalDB)
- `dotnet-ef` aracÄ± (migrasyonlar iÃ§in)

## 1) Master key (zorunlu)

Wasla, hassas verileri ÅŸifrelemek/Ã§Ã¶zmek iÃ§in bir master key ister.

PowerShell:

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
setx ENCRYPTION_MASTER_KEY $key
```

Yeni terminal aÃ§Ä±n (env deÄŸiÅŸkeni yeniden yÃ¼klensin).

## 1.1) Merkez yÃ¶netim (Web `/admin`)

Merkez yÃ¶netim, tenant kullanÄ±cÄ±larÄ±ndan **ayrÄ±** Ã§erez kimlik doÄŸrulamasÄ± kullanÄ±r. GerÃ§ek sÄ±rlarÄ± repoya koymayÄ±n; **kullanÄ±cÄ± ortam deÄŸiÅŸkenleri** ile yapÄ±landÄ±rÄ±n. Parola yalnÄ±zca **BCrypt hash** olarak saklanmalÄ±.

Hash Ã¼retmek (master key gerekmez):

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- hash-password --password "ParolaBurada!"
```

Ã–rnek env ayarÄ±:

```powershell
[Environment]::SetEnvironmentVariable("CentralAdmin__Email", "admin@wasla.local", "User")
[Environment]::SetEnvironmentVariable("CentralAdmin__PasswordHash", "<Ã¶nceki komutun BCrypt Ã§Ä±ktÄ±sÄ±>", "User")
```

Visual Studio / Rider / terminali yeniden baÅŸlatÄ±n. `/admin/login` Ã¼zerinden giriÅŸ yapÄ±n.

## 2) Build

Repo kÃ¶kÃ¼nde:

```powershell
dotnet clean
dotnet restore
dotnet build
```

## 3) VeritabanÄ± migrasyonlarÄ± (CLI)

CentralDb ile her CustomerDbâ€™nin **ayrÄ±** EF Core migrasyon geÃ§miÅŸi vardÄ±r. Yeni kod Ã§ektikten sonra ÅŸema ile uygulamanÄ±n uyumlu olmasÄ± iÃ§in migrasyonlarÄ± uygulayÄ±n (Ã¶r. `PlatformConnections` Ã¼zerinde `SupplierId`, `ExecutorEmail` gibi yeni sÃ¼tunlar).

| Komut | Ne zaman? |
|------|-----------|
| `migrate-central` | **CentralDb** model/migrasyonu deÄŸiÅŸince (merkez veritabanÄ±ndaki kayÄ±t / `Customer` tablosu). |
| `migrate-customer` | **Tek** kiracÄ±nÄ±n veritabanÄ±nÄ± gÃ¼ncellemek iÃ§in (Ã¶r. `--slug demo`). |
| `migrate-all-customers` | **CustomerDb** migrasyonu deÄŸiÅŸince â€” **tÃ¼m aktif** mÃ¼ÅŸterilere uygular (birden Ã§ok tenant varken geliÅŸtirme ortamÄ±nda en sÄ±k bu). |
| `migration-status` | Central ve her aktif mÃ¼ÅŸteri iÃ§in uygulanan ve bekleyen migrasyonlarÄ± gÃ¶sterir. |

Ã–rnekler (repo kÃ¶kÃ¼, `ENCRYPTION_MASTER_KEY` tanÄ±mlÄ±yken):

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-central
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-customer --slug demo
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-customer --customer-id "00000000-0000-0000-0000-000000000000"
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-all-customers
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migration-status
```

Ä°steÄŸe baÄŸlÄ±: `migrate-all-customers --dry-run` (sadece bekleyen listesi) ve `--only slug1 --only slug2` (slug filtresi).

**Sorun giderme:** Web veya Worker **Invalid column name** (eksik sÃ¼tun vb.) hatasÄ± verirse `migrate-all-customers` (ve merkez ÅŸemasÄ± deÄŸiÅŸtiyse `migrate-central`) Ã§alÄ±ÅŸtÄ±rÄ±n, ardÄ±ndan uygulamayÄ± yeniden baÅŸlatÄ±n.

CustomerDbâ€™ye yeni tablolar/alanlar eklendiyse (Ã¶r. `UserNotificationSettings`) ve ÅŸu tarz hatalar gÃ¶rÃ¼rseniz:
- `Invalid column name 'NewOrderSoundEnabled'`
- `Invalid object name 'UserNotificationSettings'`

Åžunu Ã§alÄ±ÅŸtÄ±rÄ±n:

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- migrate-all-customers
```

## 3.1) YÄ±kÄ±cÄ± mÃ¼ÅŸteri sÄ±fÄ±rlama/silme (CLI)

Bu komutlar **geliÅŸtirme/staging** iÃ§indir ve **yÄ±kÄ±cÄ±dÄ±r**. `--confirm` zorunludur. Production ortamÄ±nda `--force-production` olmadan Ã§alÄ±ÅŸmayÄ± reddeder.

### MÃ¼ÅŸteriyi tamamen sil (CentralDb kaydÄ± + CustomerDb drop)

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- delete-customer --slug ahmet --confirm
```

### Sadece mÃ¼ÅŸteri veritabanÄ±nÄ± sÄ±fÄ±rla (CentralDb mÃ¼ÅŸteri kaydÄ± kalÄ±r)

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- reset-customer-db --slug ahmet --confirm
```

Reset sonrasÄ± Owner admin kullanÄ±cÄ± oluÅŸturma:

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- seed-customer-admin --slug ahmet --admin-email admin@ahmet.com --admin-password "Demo123!" --admin-name "Ahmet Admin"
```

### TÃ¼m aktif mÃ¼ÅŸteri veritabanlarÄ±nÄ± sÄ±fÄ±rla

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- reset-all-customer-dbs --confirm
```

### Ã–nerilen temiz yerel akÄ±ÅŸ

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- delete-customer --slug ahmet --confirm
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- add-customer --name "Ahmet Restoran" --slug ahmet --domain ahmet.wasla.local --admin-email admin@ahmet.com --admin-password "Demo123!" --admin-name "Ahmet Admin"
```

## 4) MÃ¼ÅŸteri oluÅŸturma (CLI)

Bir tenant/customer kaydÄ± oluÅŸturur, CustomerDbâ€™yi yaratÄ±r/migrate eder ve admin kullanÄ±cÄ± ekler.

Ã–rnek:

```powershell
dotnet run --project .\src\Wasla.Cli\Wasla.Cli.csproj -- add-customer `
  --name "Demo Restoran" `
  --slug demo `
  --domain demo.local `
  --admin-email admin@demo.local `
  --admin-password "Password123!" `
  --admin-name "Demo Admin" `
  --sql-auth trusted
```

> Not: Domain Ã§Ã¶zÃ¼mlemesi `PrimaryDomain` Ã¼zerinden yapÄ±lÄ±r. Yerelde test iÃ§in hosts dosyasÄ±na eÅŸleme yapÄ±n.

Windows hosts:
- `C:\Windows\System32\drivers\etc\hosts`
- SatÄ±r ekleyin: `127.0.0.1 demo.local`

## 5) Webâ€™i Ã§alÄ±ÅŸtÄ±rma

```powershell
dotnet run --project .\src\Wasla.Web\Wasla.Web.csproj
```

TarayÄ±cÄ±:
- `https://demo.local:<port>/auth/login`

GiriÅŸ yaptÄ±ktan sonra:
- `/dashboard`
- `/platform-connections`
- `/orders`
- `/branches`

## 6) Platform baÄŸlantÄ±sÄ± ekleme

Webâ€™de:
- Platform seÃ§in
- StoreId girin
- ApiKey / ApiSecret girin

Duplicate kontrol:
- AynÄ± Platform + aynÄ± StoreId engellenir.

## 7) Worker Ã§alÄ±ÅŸtÄ±rma (sync)

```powershell
dotnet run --project .\src\Wasla.Worker\Wasla.Worker.csproj
```

Tekrar Ã§alÄ±ÅŸtÄ±rÄ±n:
- SipariÅŸler **duplicate olmamalÄ±**
- Order parent silinip tekrar eklenmemeli
- Items/Options duplicate olmamalÄ±

