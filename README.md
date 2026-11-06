<p align="center">
  <img src="assets/logo.png" width="128" alt="GSB WiFi Otomatik logosu">
</p>

<h1 align="center">GSBWIFI Otomatik Bağlantı</h1>

<p align="center">
  Windows'ta GSBWIFI "<b>İnternet yok, açık</b>" hatası verdiğinde, Wi-Fi'ı kapatıp açma işini senin yerine yapan küçük masaüstü uygulaması.
</p>

---

## Sorun

GSBWIFI'ye bağlanırken Windows sık sık şunu gösteriyor:

> **GSBWIFI — İnternet yok, açık**

Çözüm genelde bağlantıyı kesip yeniden bağlanmak. Bazen bu bir kez yetiyor, bazen saatlerce tekrar etmek gerekiyor. Bu uygulama o tekrarı otomatik yapıyor.

## Ne yapar?

1. Uygulamayı açarsın. Hemen çalışmaya başlar.
2. 4 saniyede bir **gerçek internet var mı** diye bakar. Bunun için Microsoft'un bağlantı test adresini (`msftconnecttest.com`) kullanır. Windows'un "İnternet yok" kararını verirken baktığı kaynakla aynıdır.
3. GSBWIFI'ye bağlı değilsen bağlanmayı dener.
4. Bağlısın ama internet yoksa yaklaşık 12 saniye bekler. Hâlâ yoksa bağlantıyı kesip yeniden bağlanır. İnternet gelene kadar bunu tekrarlar.
5. **İnternet gelince** bildirim gösterir ve kendini kapatır.

Pencerede anlık durum ve işlem kaydı görünür. **Arka plana al** ile görev çubuğu simgesine iner, simgeye çift tıklayınca geri gelir. **Durdur** ile döngüyü elle durdurabilirsin.

## Kullanım

1. [Releases](../../releases/latest) sayfasından `GSBWifiOtomatik.exe` dosyasını indir.
2. Çift tıkla. Kurulum gerekmez.
3. Bağlanınca kendi kapanır.

> Windows, imzasız bir uygulama olduğu için ilk açılışta "tanınmayan uygulama" uyarısı gösterebilir. **Daha fazla bilgi → Yine de çalıştır** ile geçebilirsin. Kaynak kodu bu depoda, derlemeyi kendin de yapabilirsin.

## Gereksinimler

- Windows 10 / 11
- Bilgisayarda GSBWIFI profilinin kayıtlı olması (ağa en az bir kez bağlanmış olmalısın)
- Ek bir şey yok. Uygulama Windows ile gelen .NET Framework 4 üzerinde çalışır.

## Kendin derlemek

Depoyu indirip `build.bat` dosyasını çalıştır. Windows'ta hazır bulunan `csc.exe` derleyicisini kullanır, ek bir araç gerekmez.

```bat
build.bat
```

Çıktı: `GSBWifiOtomatik.exe`

## Ayarlar

`GSBWifiOtomatik.cs` dosyasının başındaki sabitlerden değiştirilir:

| Sabit | Varsayılan | Anlamı |
|---|---|---|
| `Ssid` | `GSBWIFI` | Bağlanılacak ağın adı |
| `TickMs` | `4000` | Kontrol aralığı (milisaniye) |
| `FailsToKick` | `3` | Kaç başarısız kontrolden sonra kopar/bağlan yapılacağı |

Başka bir açık ağda da aynı sorunu yaşıyorsan `Ssid` değerini değiştirip yeniden derlemen yeterli.

## Sınırlar

- Ağ bir **giriş sayfası** (captive portal) istiyorsa uygulama onu doldurmaz. İnternet gelmediği için denemeye devam eder.
- Sadece Wi-Fi bağlantısını yönetir. Ağın tarafındaki bir kesintiyi düzeltemez. Ağ kapalıysa döngü sürer, ağ gelince bağlanır.
- Komutlar Windows'un kendi `netsh wlan` aracı üzerinden çalışır.

## Proje yapısı

```
GSBWifiOtomatik.cs   Uygulamanın tamamı (C#, Windows Forms)
build.bat            Derleme betiği
assets/
  logo.png           Logo
  app.ico            Uygulama simgesi (16–256 px)
  icon-uret.ps1      Logoyu ve simgeyi üreten betik
```

## Lisans

Kişisel kullanım için serbest. Dilediğin gibi kullanabilir, değiştirebilirsin.
