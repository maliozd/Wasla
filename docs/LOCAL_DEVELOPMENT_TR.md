# OrderHub – Yerel Geliştirme (TR)

Bu doküman, OrderHub MVP demo akışını yerelde çalıştırmak içindir.

## Önkoşullar

- .NET SDK (repo `net8.0` hedefli)
- SQL Server / LocalDB (varsayılan CentralDb bağlantısı LocalDB)
- `dotnet-ef` aracı (migrasyonlar için)

## 1) Master key (zorunlu)

OrderHub, hassas verileri şifrelemek/çözmek için bir master key ister.

PowerShell:

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
setx ENCRYPTION_MASTER_KEY $key
```

Yeni terminal açın (env değişkeni yeniden yüklensin).

## 2) Build

Repo kökünde:

```powershell
dotnet clean
dotnet restore
dotnet build
```

## 3) Veritabanı migrasyonları (CLI)

CentralDb ile her CustomerDb’nin **ayrı** EF Core migrasyon geçmişi vardır. Yeni kod çektikten sonra şema ile uygulamanın uyumlu olması için migrasyonları uygulayın (ör. `PlatformConnections` üzerinde `SupplierId`, `ExecutorEmail` gibi yeni sütunlar).

| Komut | Ne zaman? |
|------|-----------|
| `migrate-central` | **CentralDb** model/migrasyonu değişince (merkez veritabanındaki kayıt / `Customer` tablosu). |
| `migrate-customer` | **Tek** kiracının veritabanını güncellemek için (ör. `--slug demo`). |
| `migrate-all-customers` | **CustomerDb** migrasyonu değişince — **tüm aktif** müşterilere uygular (birden çok tenant varken geliştirme ortamında en sık bu). |
| `migration-status` | Central ve her aktif müşteri için uygulanan ve bekleyen migrasyonları gösterir. |

Örnekler (repo kökü, `ENCRYPTION_MASTER_KEY` tanımlıyken):

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- migrate-central
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- migrate-customer --slug demo
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- migrate-customer --customer-id "00000000-0000-0000-0000-000000000000"
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- migrate-all-customers
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- migration-status
```

İsteğe bağlı: `migrate-all-customers --dry-run` (sadece bekleyen listesi) ve `--only slug1 --only slug2` (slug filtresi).

**Sorun giderme:** Web veya Worker **Invalid column name** (eksik sütun vb.) hatası verirse `migrate-all-customers` (ve merkez şeması değiştiyse `migrate-central`) çalıştırın, ardından uygulamayı yeniden başlatın.

CustomerDb’ye yeni tablolar/alanlar eklendiyse (ör. `UserNotificationSettings`) ve şu tarz hatalar görürseniz:
- `Invalid column name 'NewOrderSoundEnabled'`
- `Invalid object name 'UserNotificationSettings'`

Şunu çalıştırın:

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- migrate-all-customers
```

## 3.1) Yıkıcı müşteri sıfırlama/silme (CLI)

Bu komutlar **geliştirme/staging** içindir ve **yıkıcıdır**. `--confirm` zorunludur. Production ortamında `--force-production` olmadan çalışmayı reddeder.

### Müşteriyi tamamen sil (CentralDb kaydı + CustomerDb drop)

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- delete-customer --slug ahmet --confirm
```

### Sadece müşteri veritabanını sıfırla (CentralDb müşteri kaydı kalır)

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- reset-customer-db --slug ahmet --confirm
```

Reset sonrası Owner admin kullanıcı oluşturma:

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- seed-customer-admin --slug ahmet --admin-email admin@ahmet.com --admin-password "Demo123!" --admin-name "Ahmet Admin"
```

### Tüm aktif müşteri veritabanlarını sıfırla

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- reset-all-customer-dbs --confirm
```

### Önerilen temiz yerel akış

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- delete-customer --slug ahmet --confirm
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- add-customer --name "Ahmet Restoran" --slug ahmet --domain ahmet.orderhub.local --admin-email admin@ahmet.com --admin-password "Demo123!" --admin-name "Ahmet Admin"
```

## 4) Müşteri oluşturma (CLI)

Bir tenant/customer kaydı oluşturur, CustomerDb’yi yaratır/migrate eder ve admin kullanıcı ekler.

Örnek:

```powershell
dotnet run --project .\src\OrderHub.Cli\OrderHub.Cli.csproj -- add-customer `
  --name "Demo Restoran" `
  --slug demo `
  --domain demo.local `
  --admin-email admin@demo.local `
  --admin-password "Password123!" `
  --admin-name "Demo Admin" `
  --sql-auth trusted
```

> Not: Domain çözümlemesi `PrimaryDomain` üzerinden yapılır. Yerelde test için hosts dosyasına eşleme yapın.

Windows hosts:
- `C:\Windows\System32\drivers\etc\hosts`
- Satır ekleyin: `127.0.0.1 demo.local`

## 5) Web’i çalıştırma

```powershell
dotnet run --project .\src\OrderHub.Web\OrderHub.Web.csproj
```

Tarayıcı:
- `https://demo.local:<port>/auth/login`

Giriş yaptıktan sonra:
- `/dashboard`
- `/platform-connections`
- `/orders`
- `/branches`

## 6) Platform bağlantısı ekleme

Web’de:
- Platform seçin
- StoreId girin
- ApiKey / ApiSecret girin

Duplicate kontrol:
- Aynı Platform + aynı StoreId engellenir.

## 7) Worker çalıştırma (sync)

```powershell
dotnet run --project .\src\OrderHub.Worker\OrderHub.Worker.csproj
```

Tekrar çalıştırın:
- Siparişler **duplicate olmamalı**
- Order parent silinip tekrar eklenmemeli
- Items/Options duplicate olmamalı

