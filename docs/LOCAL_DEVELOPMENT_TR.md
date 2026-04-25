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

## 3) Müşteri oluşturma (CLI)

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

## 4) Web’i çalıştırma

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

## 5) Platform bağlantısı ekleme

Web’de:
- Platform seçin
- StoreId girin
- ApiKey / ApiSecret girin

Duplicate kontrol:
- Aynı Platform + aynı StoreId engellenir.

## 6) Worker çalıştırma (sync)

```powershell
dotnet run --project .\src\OrderHub.Worker\OrderHub.Worker.csproj
```

Tekrar çalıştırın:
- Siparişler **duplicate olmamalı**
- Order parent silinip tekrar eklenmemeli
- Items/Options duplicate olmamalı

