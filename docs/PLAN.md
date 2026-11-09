# İkinci Ekran Projesi — Mimari ve Uygulama Planı (`PLAN.md`)

Bu belge, bir Windows dizüstü bilgisayarı (PC) ile Samsung Android tableti düşük gecikmeli (30–80 ms) ikinci ekran olarak bağlayan sistemin uçtan uca yazılım mimarisini, teknoloji seçimlerini ve adım adım geliştirme planını içerir.

> [!IMPORTANT]
> **Kapsam Özeti (Kullanıcı Kararları Doğrultusunda):**
> - **Görüntü + Dokunma:** Yalnızca görüntü aktarımı ve parmak dokunuşu (tekli/çoklu dokunma) kapsam dahilindedir. Ses aktarımı ve S-Pen (kalem basıncı/eğim) ilk sürümde **kapsam dışıdır**.
> - **Dizüstü Hibrit Grafik Desteği:** Bilgisayar dizüstü olduğu için dahili (Intel/AMD iGPU) ve harici (NVIDIA/AMD dGPU) ekran kartları bir arada bulunabilir. İlk aşamada tüm GPU'larda çalışan **Windows Media Foundation (Donanımsal MFT)** kullanılacak; NVENC gibi karta özel SDK optimizasyonları sonraya bırakılacaktır.
> - **Ağ Kısıtlaması:** Yurt Wi-Fi ağlarında **Client Isolation** (istemci izolasyonu) bulunabileceği için yerel ağ (LAN) moduna körü körüne güvenilmeyecek; USB-C, Windows Mobil Etkin Nokta (Hotspot) ve Wi-Fi Direct modları birinci sınıf vatandaş olarak tasarlanacaktır.

---

## 1. Başlangıç İçin Temel Kavramlar (Jargon Sözlüğü)

Projeye başlamadan önce karşınıza çıkacak teknik terimlerin sade açıklamaları:

| Terim | Ne Anlama Gelir? | Projede Neden Önemli? |
| :--- | :--- | :--- |
| **Gecikme (Latency)** | PC ekranında bir pikselin değiştiği an ile o değişimin tablet ekranında göründüğü an arasında geçen süre (milisaniye - ms). | 30–80 ms arası akıcı hissettirir. 150 ms üstünde fare imleci "lastik gibi" geriden gelir. |
| **Codec (H.264 / AVC)** | Ham görüntüyü sıkıştıran (Encoder) ve çözen (Decoder) algoritma. | Sıkıştırılmamış 1080p 60 FPS video saniyede ~3 Gigabit veri üretir (Wi-Fi taşıyamaz). H.264 bunu saniyede 10–20 Megabit'e düşürür. |
| **I-Frame (IDR / Anahtar Kare)** | Tek başına tam bir fotoğraf gibi çözülebilen video karesi. | Bağlantı ilk kurulduğunda veya bozulduğunda görüntüyü sıfırdan oluşturmak için şarttır ama boyutu büyüktür. |
| **P-Frame (Fark Karesi)** | Sadece bir önceki kareden bu yana değişen pikselleri taşıyan küçük kare. | Düşük gecikmeli yayınların %98'i P-frame'dir. Küçüktür, ağdan çok hızlı geçer. |
| **B-Frame (Çift Yönlü Kare)** | Hem önceki hem de **sonraki** kareye bakarak sıkıştırma yapan kare. | **BİZDE KAPALI OLACAK.** Çünkü "sonraki kareyi" beklemek zorunda olduğu için doğrudan +30 ile +100 ms arası fazladan gecikme yaratır. |
| **SPS / PPS** | Video çözücüye (Decoder) "Bu videonun çözünürlüğü, renk formatı ve profili şudur" diyen birkaç baytlık ayar paketi. | Android `MediaCodec`, ilk görüntüyü çözmeden önce mutlaka SPS ve PPS paketlerini almak zorundadır. |
| **Media Foundation (MF)** | Windows'un ekran kartı üzerindeki donanımsal video sıkıştırıcıları (Intel, AMD, NVIDIA) tek bir standart arayüzle kullanmamızı sağlayan sistem. | Karta özel kod yazmadan donanımsal H.264 sıkıştırma yapmamızı sağlar. |
| **MediaCodec** | Android'in tablet içindeki donanımsal video çözücü çipine erişen resmi API'si. | Tabletin işlemcisini (CPU) yormadan ve pilini bitirmeden 60 FPS video çözer. |
| **adb reverse** | Android Hata Ayıklama Köprüsü (ADB) üzerinden USB kablosu içinden sanal ağ tüneli açan komut. | Tabletteki uygulama `127.0.0.1` (kendi içi) adresine bağlandığında, veriyi USB kablosundan doğrudan PC'deki programa ulaştırır. |
| **Client Isolation (İstemci İzolasyonu)** | Yurt, kafe ve okul Wi-Fi ağlarında aynı modeme bağlı iki cihazın birbirini görmesini güvenlik amacıyla engelleyen duvar. | Yurt Wi-Fi'sine hem PC hem tablet bağlı olsa bile birbirlerine paket gönderemezler. Bu yüzden Hotspot ve Wi-Fi Direct şarttır. |
| **TCP_NODELAY (Nagle Algoritması)** | TCP'nin küçük paketleri birleştirip tek seferde göndermek için birkaç milisaniye bekletme huyu (Nagle). | `TCP_NODELAY = true` yaparak bu bekletmeyi kapatırız; her video karesi ve dokunma verisi anında yola çıkar. |
| **IddCx (Indirect Display Driver)** | Windows 10/11'in kasaya fiziksel kablo takılı olmadan işletim sistemini "yeni bir monitör takıldı" diye ikna eden resmi sanal ekran sürücü altyapısı. | Sadece mevcut ekranı kopyalamak (yansıtmak) yerine masaüstünü sağa/sola **uzatmak (Extend)** için gereklidir. |

---

## 2. Klasör ve Proje Yapısı

Proje kök dizininde (`d:\Areas\`) aşağıdaki yapı kullanılacaktır. Bağlantı yöntemi (Transport), görüntü sıkıştırma/çözme (Video) ve mesajlaşma (Protocol) birbirinden kesin sınırlarla ayrılmıştır:

```text
/ (Proje Kökü)
├── docs/
│   ├── PLAN.md                        # Mimari, teknoloji seçimi ve 6 aşamalı yol haritası (Bu dosya)
│   └── PROTOCOL.md                    # İkili (binary) haberleşme protokolü şartnamesi
│
├── protocol/
│   ├── schema-v1.md                   # Protokol sabitleri ve test vektörleri (örnek paket dizileri)
│   └── test-vectors/                  # Hem C# hem Kotlin birim testlerinin doğrulayacağı örnek binary dosyalar
│
├── windows-host/                      # Windows Gönderici Uygulaması (.NET 8 / C#)
│   ├── WindowsHost.sln
│   ├── src/
│   │   ├── SecondScreen.Core/         # Ortak arayüzler, protokol serileştirici, zamanlayıcılar
│   │   │   ├── Protocol/              # Paket okuma/yazma (PacketReader, PacketWriter, FrameHeader)
│   │   │   ├── Transport/             # ITransport arayüzü, TcpTransport, AdbHelper
│   │   │   └── Diagnostics/           # Gecikme ölçümü, FPS sayacı, loglama
│   │   ├── SecondScreen.Media/        # Ekran yakalama ve H.264 sıkıştırma
│   │   │   ├── Capture/               # Windows.Graphics.Capture / DXGI ekran yakalayıcı
│   │   │   └── Encoding/              # Media Foundation H.264 donanımsal encoder
│   │   ├── SecondScreen.Input/        # Dokunma girdisi enjektörü (Win32 InjectTouchInput)
│   │   ├── SecondScreen.Network/      # LAN keşif, Mobil Hotspot kontrolü, Wi-Fi Direct yöneticisi
│   │   ├── SecondScreen.Display/      # Sanal monitör (IddCx) kontrol ve ekran konumlandırma
│   │   └── SecondScreen.App/          # Masaüstü arayüzü (WPF veya WinUI 3) / Komut satırı (CLI)
│   └── tests/
│       └── SecondScreen.Tests/        # Protokol, paketleme ve sahte (mock) transport birim testleri
│
└── android-client/                    # Android Alıcı Uygulaması (Kotlin / Android Studio)
    ├── build.gradle.kts
    └── app/
        └── src/
            ├── main/java/com/secondscreen/client/
            │   ├── protocol/          # Paket ayrıştırıcı (PacketParser,PacketSerializer)
            │   ├── transport/         # Transport arayüzü, SocketTransport, WifiDirectTransport
            │   ├── decoder/           # Low-Latency MediaCodec H.264 çözücü ve Surface yönetimi
            │   ├── input/             # MotionEvent yakalayıcı ve koordinat dönüştürücü
            │   ├── telemetry/         # Ekranda gecikme/FPS göstergesi (Overlay HUD)
            │   └── ui/                # Bağlantı seçimi ekranı ve tam ekran video yüzeyi (SurfaceView)
            └── test/                  # Protokol ayrıştırma ve koordinat normalizasyon birim testleri
```

---

## 3. Teknoloji Seçimleri ve Gerekçeleri

### 3.1. Windows Tarafı Neden C# (.NET 8)? (C# vs C++ Karşılaştırması)

Windows gönderici uygulaması için **C# (.NET 8)** seçilmiştir.

**Neden C++ Değil de C#?**
1. **Yeni Başlayan Dostu ve Güvenli:** C++'ta bellek yönetimi (pointer hataları, memory leak, buffer overflow) ve çok iş parçacıklı (multi-thread) ağ programlama yeni başlayan biri için projeyi haftalarca kilitleyebilir. C# bellek güvenliğini otomatik sağlar ve `async/await` yapısıyla ağ bağlantısı kopmalarını yönetmeyi çok kolaylaştırır.
2. **Performans Kaybı Yoktur:** "C# yavaş kalır mı?" endişesi bu projede geçerli değildir. Çünkü pikselleri CPU'da C# koduyla tek tek işlemiyoruz. Ekran yakalama (`Direct3D11`) ve H.264 sıkıştırma (`Media Foundation`) doğrudan ekran kartı (GPU) üzerinde çalışır. C# sadece ekran kartına "yakala, sıkıştır ve bana oluşan küçük bayt dizisini ver" komutunu iletir (`Vortice.Windows` kütüphanesi ile sıfır kopyalama / zero-copy COM çağrıları yapılır).
3. **Protokol ve Ağ Hızı:** .NET 8 içindeki `Span<byte>` ve `BinaryPrimitives` yapıları, C++ kadar hızlı (bellek tahsisi yapmadan / zero-allocation) paket okuma/yazma imkanı sunar.
4. **Sanal Sürücü İstisnası (Aşama 6):** Windows sanal ekran sürücüsü (`IddCx`) işletim sistemi kuralları gereği C++ olmak zorundadır. Ancak Aşama 6'da sıfırdan sürücü yazmayacağız (çünkü Microsoft dijital imza sertifikası binlerce dolar tutar); hazır imzalı açık kaynak bir sürücüyü (Virtual Display Driver) kurup C# uygulamamızdan yöneteceğiz.

### 3.2. Dizüstü Hibrit Grafik (Optimus) ve Ekran Yakalama Seçimi

Dizüstü bilgisayarlarda genellikle iki ekran kartı bulunur:
- **Dahili GPU (iGPU - Intel/AMD):** Pil tasarrufu için masaüstünü ve ekran panelini çizer.
- **Harici GPU (dGPU - NVIDIA/AMD):** Oyunları ve ağır programları çalıştırır.

> [!WARNING]
> **Hibrit Grafik Tuzağı:** Eski yöntem olan `DXGI Desktop Duplication API` kullanılırken program yanlışlıkla harici (NVIDIA) kart üzerinde başlatılırsa, masaüstü dahili (Intel) kartta çizildiği için Windows `DXGI_ERROR_UNSUPPORTED` hatası verir ve ekran yakalanamaz.

**Çözümümüz:**
- Ekran yakalama için öncelikle modern **`Windows.Graphics.Capture` (WGC)** API'si (veya masaüstü/sanal monitörün bağlı olduğu doğru `IDXGIAdapter` adaptörünü otomatik bulan `Vortice.DXGI` katmanı) kullanılacaktır.
- Yakalanan kare GPU belleğinden (`ID3D11Texture2D`) hiç RAM'e (CPU'ya) indirilmeden doğrudan aynı GPU üzerindeki **Media Foundation H.264 Encoder**'a verilecektir.

### 3.3. Görüntü Sıkıştırma (H.264) ve Düşük Gecikme Ayarları

30–80 ms hedefine ulaşmak için hem Windows hem Android tarafında varsayılan video ayarları **kullanılamaz** (varsayılan ayarlar kalite odaklıdır ve 300–500 ms gecikme yapar). Zorunlu ayarlarımız:

1. **Windows Media Foundation (Encoder) Ayarları:**
   - `CODECAPI_AVLowLatencyMode = TRUE` (Sıkıştırıcı içi kare biriktirmeyi kapatır, giren kare anında çıkar).
   - `CODECAPI_AVEncMPVDefaultBPictureCount = 0` (**B-Frame tamamen kapalı**).
   - `CODECAPI_AVEncCommonRateControlMode = CBR` (Sabit bit hızı veya düşük gecikmeli VBR; ani sahne geçişlerinde tampon şişmesini önler).
   - Profil: `eAVEncH264VProfile_Base` (Constrained Baseline) veya `Main` — çözme karmaşıklığını en aza indirir.
2. **Android MediaCodec (Decoder) Ayarları:**
   - Android 11+ (API 30+) için `MediaFormat.KEY_LOW_LATENCY = 1` (Donanımsal çözücüyü ilk kareyi alır almaz ekrana basmaya zorlar).
   - Satıcıya özel yedek bayraklar (Samsung Exynos / Qualcomm Snapdragon için): `vendor.qti-ext-dec-low-latency.enable = 1`, `vendor.rtc-ext-dec-low-latency.enable = 1`.
   - Çıktı yüzeyi olarak `TextureView` yerine doğrudan donanım birleştiricisine (Hardware Composer) giden **`SurfaceView`** kullanılacaktır (`TextureView` ekstra 1-2 kare gecikme ekler).

### 3.4. "Transport" (Taşıma) Soyutlama Mimarisi

Görüntü sıkıştırma, protokol paketleme ve dokunma kodları **bağlantının USB mi, LAN mı, yoksa Hotspot mu olduğunu bilmeyecektir.**

```mermaid
flowchart LR
    subgraph PC ["Windows Gönderici (C#)"]
        Cap["Ekran Yakalama\n(D3D11)"] --> Enc["H.264 Encoder\n(Media Foundation)"]
        Enc --> ProtoPC["Protokol Katmanı\n(Framing)"]
        TouchPC["Win32 Dokunma\nEnjektörü"] <-- ProtoPC
        ProtoPC <--> TransPC["ITransport Arayüzü"]
    end

    subgraph Modes ["3 Bağlantı Modu (Tak-Çıkar)"]
        M1["1. USB-C\n(adb reverse TCP)"]
        M2["2. Wi-Fi LAN\n(Yerel Ağ Soketi)"]
        M3["3. Özel Bağlantı\n(Hotspot / Wi-Fi Direct)"]
    end

    subgraph Tablet ["Android Alıcı (Kotlin)"]
        TransAnd["Transport Arayüzü"] <--> ProtoAnd["Protokol Katmanı\n(Parser)"]
        ProtoAnd --> Dec["H.264 Decoder\n(MediaCodec Low-Latency)"]
        Dec --> Surf["SurfaceView\n(Tablet Ekranı)"]
        TouchAnd["MotionEvent\nYakalayıcı"] --> ProtoAnd
    end

    TransPC <--> M1 & M2 & M3 <--> TransAnd
```

---

## 4. Aşama Listesi ve Uygulama Planı

Geliştirme süreci, her aşamanın sonunda çalışan ve test edilebilen bir ürün çıkacak şekilde **6 aşamaya** bölünmüştür. Bir aşama bitmeden diğerine geçilmez.

---

### Aşama 1: USB-C ile Ekran Yansıtma (`adb reverse`)

**Hedef:** En güvenilir ve paket kaybı olmayan bağlantı yöntemi olan USB kablosu üzerinden PC ana ekranını Samsung tablete 60 FPS'te yansıtmak.

#### Görev Listesi
1. **Ortak Protokol Katmanı:** `docs/PROTOCOL.md` içinde tanımlanan başlık (Header), `HELLO`, `HELLO_ACK`, `VIDEO_CONFIG` ve `VIDEO_FRAME` mesaj sınıflarını C# ve Kotlin tarafında oluşturmak.
2. **USB Transport (`TcpTransport`):**
   - PC tarafında `127.0.0.1:27183` portunu dinleyen (`TcpListener`) ve `Socket.NoDelay = true` ayarlı taşıyıcıyı kurmak.
   - PC uygulamasının başlangıçta otomatik olarak `adb reverse tcp:27183 tcp:27183` komutunu çalıştıran yardımcı sınıfını (`AdbHelper`) hazırlamak.
   - Android tarafında `127.0.0.1:27183` adresine bağlanan `SocketTransport` sınıfını kurmak.
3. **PC Ekran Yakalama ve Sıkıştırma:**
   - Birincil ekranı 1920x1080 (veya 1280x720 başlangıç testi) 60 FPS yakalayan modülü kurmak.
   - Media Foundation H.264 MFT'yi B-frame kapalı ve Low-Latency açık şekilde yapılandırıp ilk çıkan `SPS/PPS` verisini `VIDEO_CONFIG`, sonraki kareleri `VIDEO_FRAME` olarak paketlemek.
4. **Android Video Çözme:**
   - Tam ekran bir `SurfaceView` oluşturmak.
   - Gelen `VIDEO_CONFIG` paketindeki SPS/PPS verisiyle `MediaCodec` (`video/avc`) çözücüsünü düşük gecikme bayrağıyla başlatmak ve gelen `VIDEO_FRAME` paketlerini çözüp ekrana basmak.

#### "Bitti" Ölçütü (Definition of Done)
- USB kablosu takılıyken PC uygulaması ve Android uygulaması başlatıldığında 3 saniye içinde el sıkışma (`HELLO` -> `HELLO_ACK`) tamamlanır.
- PC ekranındaki hareketli görüntü (ör. fare hareketi veya video) tablet ekranında bozulma (yeşil ekran, pikselleşme) olmadan en az 10 dakika kesintisiz akar.
- Kablo çekilip takıldığında uygulama çökmez, bağlantı koptuğunu ekranda bildirir.

#### Otomatik Test Edilebilenler
- **Protokol Birim Testleri (C# & Kotlin):** Örnek bir `VIDEO_FRAME` mesajının C# tarafında baytlara dönüştürülüp (`Serialize`), aynı bayt dizisinin Kotlin birim testinde birebir aynı başlık ve veriyle okunduğunun (`Deserialize`) doğrulanması.
- **Parçalı Paket (TCP Stream Fragmentation) Testi:** 50 KB'lık bir video karesinin sahte (Mock) soket üzerinden 1 KB'lık parçalar halinde gönderildiğinde alıcının paketi eksiksiz birleştirdiğinin testi.

#### Elle Test Adımları (Kullanıcı İçin Adım Adım)
1. Samsung tablette **Ayarlar > Telefon/Tablet Hakkında > Yazılım Bilgileri** menüsüne girin ve **Yapım Numarası (Build Number)** üzerine 7 kez dokunarak **Geliştirici Seçenekleri**'ni açın.
2. **Ayarlar > Geliştirici Seçenekleri** içinden **USB Hata Ayıklama (USB Debugging)** seçeneğini aktif edin.
3. Tableti USB-C kablosuyla PC'ye bağlayın ve tablet ekranına gelen *"USB hata ayıklamaya izin verilsin mi?"* uyarısında **"Her zaman izin ver"** kutucuğunu işaretleyip onaylayın.
4. PC'de terminali açıp `adb devices` yazın; cihazınızın yanında `device` yazdığını görün.
5. PC gönderici uygulamasını başlatın (otomatik olarak `adb reverse` kuracaktır).
6. Tablette alıcı uygulamayı açıp **"USB ile Bağlan"** butonuna dokunun.
7. PC'de bir pencereyi sürükleyin ve tablet ekranında eşzamanlı hareket ettiğini gözlemleyin.

#### Bilinen Riskler
- **Risk 1 (Hibrit GPU Yakalama Hatası):** Dizüstü bilgisayarda yakalayıcı yanlış ekran kartında başlarsa siyah ekran gelebilir. *Çözüm:* Hata kodunu yakalayıp sistemdeki diğer `IDXGIAdapter` (Intel/AMD iGPU) üzerinden otomatik yeniden deneme yapılacak.
- **Risk 2 (İlk Kareyi Kaçırma):** Android çözücü SPS/PPS ve ilk I-Frame'i (Keyframe) almazsa ekran siyah kalır. *Çözüm:* Her yeni bağlantıda PC kodlayıcısı zorunlu olarak önce `SPS/PPS` ve hemen ardından yeni bir `IDR (I-Frame)` üretecek.

---

### Aşama 2: Gecikme Ölçümü (Uçtan Uca ve Aşama Bazlı)

**Hedef:** "Hızlı görünüyor" gibi öznel tahminler yerine, gecikmenin kaç milisaniye olduğunu ve hangi adımda (yakalama, sıkıştırma, ağ, çözme) vakit kaybedildiğini sayısal olarak ölçmek. Hedef: **30–80 ms**.

#### Görev Listesi
1. **Aşama Bazlı (Pipeline) Kronometreler:**
   - **PC Tarafı:** `T_capture` (ekranın yakalandığı an) ve `T_encode_done` (H.264 sıkıştırmanın bittiği an) farkını ölçmek (`Encode Süresi`).
   - **Ağ Saati Eşitleme (`PING` / `PONG`):** Her saniye küçük bir zaman damgalı paket gönderip gidiş-dönüş süresini (`RTT / 2`) ölçmek.
   - **Android Tarafı:** Paketin soketten okunduğu an (`T_recv`) ile `MediaCodec.releaseOutputBuffer` çağrıldığı an (`T_render`) arasındaki farkı ölçmek (`Decode Süresi`).
2. **Tablet Ekran Üstü Gösterge (HUD Overlay):** Tablet ekranının sağ üst köşesine açılıp kapatılabilen yarı saydam bir bilgi kutusu eklemek (`FPS`, `Bitrate Mbps`, `Encode ms`, `RTT/2 ms`, `Decode ms`).
3. **Kronometre (Görsel Uçtan Uca) Modu:** PC uygulamasında ekrana milisaniye hassasiyetinde dev bir dijital sayaç çizen "Gecikme Test Penceresi" eklemek.

#### "Bitti" Ölçütü
- Tablet ekranındaki HUD göstergesinde her saniye güncellenen `Encode`, `Ağ (RTT/2)` ve `Decode` süreleri görünür.
- Telefon kamerasıyla yapılan görsel kronometre testinde PC ve tablet ekranları arasındaki fark USB modunda **30–60 ms** aralığında ölçülür.

#### Otomatik Test Edilebilenler
- `PING` / `PONG` mesajlarıyla saat farkı (clock offset) ve `RTT` hesaplama algoritmasının yapay gecikmeli (simüle edilmiş 20 ms) transport üzerinde doğru sonuç verdiğinin birim testi.
- Kayan ortalama (Rolling Average / P95) hesaplayıcısının birim testi.

#### Elle Test Adımları (Kullanıcı İçin Adım Adım — Kamera Kronometre Testi)
1. USB bağlantısını başlatın ve tablette görüntünün aktığını doğrulayın.
2. PC uygulamasında **"Gecikme Test Kronometresini Aç"** butonuna tıklayın (ekranda `00:14.582` gibi milisaniyelik akan büyük bir sayaç açılır).
3. Tableti fiziksel olarak PC ekranının hemen yanına koyun (her iki ekrandaki sayaç aynı anda görünsün).
4. Cep telefonunuzun kamerasını açın (varsa **Ağır Çekim / Slow Motion** moduna alın, yoksa normal fotoğraf modunda yüksek ışıkta çekin) ve her iki ekranı aynı karede fotoğraflayın.
5. Çektiğiniz fotoğrafta PC ekranındaki sayıdan (ör. `14.580`) tablet ekranındaki sayıyı (ör. `14.535`) çıkarın (`14580 - 14535 = 45 ms`). Bu sayı sizin **gerçek uçtan uca gecikmenizdir**.

#### Bilinen Riskler
- **Risk (MediaCodec Kuyruk Şişmesi):** PC anlık olarak 60 FPS üretirken Android 55 FPS çözerse, soket tamponunda kareler birikir ve gecikme 50 ms'den 500 ms'ye fırlar. *Çözüm:* Gönderici tarafında kuyrukta bekleyen gönderilmemiş kare varsa eski kare atılacak (Frame Drop) ve Android tarafında çözücü kuyruğu 1 kareden fazla bekletilmeyecek.

---

### Aşama 3: Wi-Fi LAN Bağlantısı

**Hedef:** Ev ağı gibi cihazların birbirini görebildiği yerel Wi-Fi ağlarında kablosuz bağlantı kurmak; yurt ağı gibi **Client Isolation** olan ağlarda ise durumu 2 saniye içinde tespit edip kullanıcıyı uyarmak.

#### Görev Listesi
1. **LAN Transport (`LanTcpTransport`):**
   - Mevcut `TcpTransport` sınıfını `0.0.0.0:27183` (tüm yerel IP'ler) üzerinden dinleyecek şekilde genişletmek (kodun %95'i Aşama 1 ile aynıdır).
2. **Bağlantı Kolaylaştırıcı (UDP Broadcast + Manuel IP / QR):**
   - PC'nin yerel IP adreslerini (`192.168.x.x`) arayüzde büyük puntolarla göstermek ve isteğe bağlı QR kod çizmek.
   - Yerel ağda UDP `27184` portundan saniyede bir *"Ben buradayım"* yayını (Broadcast) yapmak (eğer ağ izin veriyorsa tablette PC otomatik listelenir).
3. **Client Isolation (Yurt Ağı) Tespit Mekanizması:**
   - Tablet aynı Wi-Fi'de olmasına rağmen 2.5 saniye içinde TCP bağlantısı kurulamazsa (`ConnectTimeout`), tablette doğrudan: *"Ağ cihazlar arası iletişimi engelliyor olabilir (Yurt/Okul Wi-Fi). Lütfen USB veya Mobil Etkin Nokta modunu seçin."* uyarısı çıkarmak.

#### "Bitti" Ölçütü
- Ev Wi-Fi ağında (veya telefon internet paylaşımında) kablo takılı değilken IP girerek veya listeden seçerek bağlantı kurulur.
- Gecikme 5 GHz Wi-Fi üzerinde **45–80 ms** aralığında kalır.
- Bağlantı koptuğunda (Wi-Fi kapatılıp açıldığında) otomatik yeniden bağlanma (`Reconnect`) devreye girer.

#### Otomatik Test Edilebilenler
- Yerel ağ geri döngüsü (`localhost` dışı yerel IP) üzerinden bağlantının kurulması, kopması ve `HELLO` el sıkışmasının otomatik yeniden bağlanma (backoff) testleri.
- Zaman aşımı (`Timeout`) durumunda izolasyon hatasının doğru fırlatıldığının testi.

#### Elle Test Adımları (Kullanıcı İçin Adım Adım)
1. USB kablosunu tabletten çıkarın.
2. PC ve tableti aynı 5 GHz Wi-Fi ağına bağlayın (Ev ağı veya cep telefonunuzun hotspot'u; yurt ağında izolasyon uyarısını görmek için de deneyebilirsiniz).
3. PC uygulamasında **"Wi-Fi LAN Modu"**nu başlatın ve ekranda yazan IP adresini (ör. `192.168.1.42`) not alın (Windows Güvenlik Duvarı izin isterse *"Özel ve Ortak ağlara izin ver"* kutularını işaretleyin).
4. Tablette uygulamayı açın: PC otomatik listede çıktıysa üzerine dokunun; çıkmadıysa IP adresini yazıp **"Bağlan"**a dokunun.
5. Aşama 2'deki HUD göstergesinden kablosuz gecikmeyi kontrol edin.

#### Bilinen Riskler
- **Risk 1 (Windows Güvenlik Duvarı - Firewall):** Yurt veya ortak ağlarda Windows ağ profilini "Public (Ortak)" olarak işaretler ve gelen bağlantıları sessizce engeller. *Çözüm:* Kurulum/ilk çalıştırma sırasında `netsh advfirewall` ile uygulama portuna kural ekleme komutu kullanıcı onayına sunulacak.
- **Risk 2 (TCP Head-of-Line Blocking — DOĞRULANMADI):** Kalabalık yurt ortamlarında 2.4 GHz/5 GHz radyo paraziti nedeniyle paket kaybı olursa, TCP kayıp paketi tekrar isteyene kadar sonraki kareleri bekletir (anlık takılma). *Strateji:* İlk olarak `TCP_NODELAY` ve küçük gönderim tamponu (`SendBufferSize = 64KB`) ile test edilecek; takılma gözlenirse `ITransport` arkasına sadece video kareleri için UDP seçeneği eklenecek (**DOĞRULANMADI — Aşama 3 testinde ölçülecek**).

---

### Aşama 4: İnternetsiz Özel Bağlantı (Önce Mobil Etkin Nokta, Sonra Wi-Fi Direct)

**Hedef:** Yurt ağında Client Isolation varken ve hiç internet yokken bile PC ile tableti doğrudan kablosuz bağlamak.

> [!IMPORTANT]
> **Mimari Strateji (Neden Önce Mobil Etkin Nokta, Sonra Wi-Fi Direct?):**
> Windows 10/11'in yerleşik **Mobil Etkin Nokta (Mobile Hotspot)** özelliği, arka planda aslında Wi-Fi Direct sanal adaptörünü (`Microsoft Wi-Fi Direct Virtual Adapter`) kullanarak standart bir WPA2 erişim noktası açar. Android tablet buna normal bir Wi-Fi ağı gibi bağlanır ve her zaman sabit bir alt ağ (`192.168.137.1`) alır.
> Saf **Wi-Fi Direct (P2P)** eşleşmesi ise Windows WinRT API'si ile Android `WifiP2pManager` arasında grup yöneticisi (Group Owner) seçimi ve WPS el sıkışması sırasında sürücüye bağlı uyumsuzluklar çıkarabilmektedir (**DOĞRULANMADI**). Bu nedenle önce %100 çalışan Mobil Etkin Nokta tamamlanacak, ardından saf Wi-Fi Direct eklenecektir.

#### Görev Listesi
1. **Aşama 4A — Windows Mobil Etkin Nokta (Hotspot) Modu:**
   - PC uygulamasında `Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager` API'sini (veya kullanıcı yönlendirmesini) kullanarak internetsiz yerel Hotspot başlatma desteği.
   - `192.168.137.1` arayüzünü otomatik algılayıp dinlemeye başlayan transport profili.
2. **Aşama 4B — Saf Wi-Fi Direct (P2P) Modu (DOĞRULANMADI):**
   - **Android Tarafı:** `WifiP2pManager.createGroup()` ile tableti "Group Owner (GO)" olarak başlatmak (Android GO olduğunda her zaman sabit `192.168.49.1` IP adresini alır, böylece karmaşık eşleşme sorunları azalır) veya Windows ilanını dinlemek.
   - **Windows Tarafı:** `WiFiDirectAdvertisementPublisher` / `WiFiDirectDevice` ile bağlantıyı kurmak.

#### "Bitti" Ölçütü
- Bilgisayarda hiçbir internet bağlantısı yokken (veya yurt Wi-Fi'sine bağlıyken), tablet PC'ye doğrudan kablosuz bağlanır ve 30–80 ms gecikmeyle görüntü aktarır.

#### Otomatik Test Edilebilenler
- Ağ adaptörü seçicisinin birden fazla IP adresi varken (Yurt Wi-Fi `10.x.x.x` + Hotspot `192.168.137.1`) doğru sanal adaptör IP'sini önceliklendirdiğinin birim testi.

#### Elle Test Adımları (Kullanıcı İçin Adım Adım)
1. **4A (Mobil Etkin Nokta Testi):**
   - Windows 11'de sağ alttaki Hızlı Ayarlar panelinden (veya bizim PC uygulamasındaki **"Hotspot Başlat"** butonuyla) **Mobil Etkin Nokta**'yı açın (5 GHz bandını seçin). *Not: İnternet paylaşımı olmasa da Windows yerel ağ kurar.*
   - Samsung tabletin Wi-Fi menüsünden PC'nin oluşturduğu ağa bağlanın (Tablet *"İnternet kullanılamıyor, bağlı kalınsın mı?"* diye sorarsa **"Her zaman bağlan / Bağlı kal"** seçeneğine dokunun).
   - Tablette uygulamayı açıp **"Hotspot Modu (192.168.137.1)"** butonuna dokunun ve görüntünün geldiğini doğrulayın.
2. **4B (Wi-Fi Direct Testi):**
   - Tablette Wi-Fi açıkken uygulama içinden **"Wi-Fi Direct Grubu Başlat"** butonuna dokunun.
   - PC uygulamasından cihazı taratıp bağlanın ve onay penceresini kabul edin.

#### Bilinen Riskler ve Doğrulanmamış Noktalar
- **DOĞRULANMADI — Windows "İnternet Yoksa Hotspot Açmama" Kısıtlaması:** Bazı Windows sürümlerinde PC hiç bir ağa bağlı değilken Windows Ayarlar menüsündeki "Mobil Etkin Nokta" butonu gri (pasif) kalabilir. *Çözüm:* WinRT API / PowerShell üzerinden Loopback adaptörüyle tetikleme veya 4B'deki Android Group Owner (`WifiP2pManager.createGroup`) yöntemiyle tabletin otonom erişim noktası açması sağlanacaktır. Bu davranış kullanıcının dizüstü Wi-Fi kartı sürücüsünde Aşama 4 başında doğrulanacaktır.
- **DOĞRULANMADI — Windows-Android Wi-Fi Direct WPS Uyumsuzluğu:** Intel AX200/AX210 Wi-Fi kartları ile Samsung OneUI Wi-Fi Direct eşleşmesinde bazen PIN/PushButton diyaloğu düşmeyebilir. Ancak Android'de `createGroup()` + `Passphrase` okuma yöntemiyle Windows'un normal Wi-Fi istemcisi gibi bağlanması (Legacy Client Mode) bu riski ortadan kaldırır.

---

### Aşama 5: Dokunma Girdisi Geri Gönderimi (Parmak Dokunuşu)

**Hedef:** Tablet ekranına parmakla dokunulduğunda, kaydırıldığında veya iki parmakla yakınlaştırma (pinch-to-zoom) yapıldığında bunun PC'de gerçek bir dokunmatik ekran dokunuşu olarak anında çalışması.

#### Görev Listesi
1. **Android Dokunma Yakalayıcı (`TouchCaptureView`):**
   - `SurfaceView.onTouchEvent(MotionEvent)` üzerinden `ACTION_DOWN`, `ACTION_POINTER_DOWN`, `ACTION_MOVE`, `ACTION_POINTER_UP`, `ACTION_UP`, `ACTION_CANCEL` olaylarını yakalamak.
   - Koordinatları tabletin piksel çözünürlüğünden bağımsız hale getirmek için `0` ile `65535` (`uint16`) arasına normalize etmek (böylece PC çözünürlüğü değişse de dokunma noktası şaşmaz).
   - Siyah kenar boşluklarını (Letterbox / Pillarbox — ör. 16:10 tablette 16:9 PC görüntüsü) hesaba katarak sadece aktif görüntü alanı içindeki dokunuşları çevirmek.
2. **Protokol Mesajı (`TOUCH_EVENT`):**
   - Çoklu dokunma (Multi-touch, en fazla 10 parmak) verisini tek bir küçük pakette (~16–32 bayt) anında PC'ye göndermek.
3. **Windows Dokunma Enjektörü (`Win32TouchInjector`):**
   - Windows `User32.dll` içindeki `InitializeTouchInjection(10, TOUCH_FEEDBACK_DEFAULT)` ve `InjectTouchInput` API'lerini kullanarak işletim sistemine gerçek çoklu dokunma olayları göndermek.
   - Koordinatları yansıtılan (veya Aşama 6'daki sanal) monitörün masaüstü sanal ekran koordinatlarına (`SM_XVIRTUALSCREEN`, `SM_YVIRTUALSCREEN`) eşlemek.

#### "Bitti" Ölçütü
- Tablette bir butona dokunulduğunda PC'de tıklanır.
- Tablette parmak sürüklenerek pencere taşınabilir veya web sayfası kaydırılabilir.
- İki parmakla kıstırma/açma (Pinch Zoom) hareketi Windows Fotoğraflar veya tarayıcıda yakınlaştırma yapar.
- Dokunma gecikmesi hissi (parmak hareketi ile tepki arası) video gecikmesiyle uyumlu olur.

#### Otomatik Test Edilebilenler
- **Letterbox / Koordinat Dönüşüm Testleri:** 2560x1600 (16:10) bir tablette 1920x1080 (16:9) görüntü ortalanmışken, üst/alt siyah bantlara dokunulduğunda yoksayıldığının ve tam merkeze (`1280, 800`) dokunulduğunda tam olarak `(32767, 32767)` üretildiğinin birim testi.
- **Basılı Tutulan Parmak Kurtarma (Stuck Pointer) Testi:** Parmak ekrana basılıyken (`DOWN`) ağ bağlantısı koparsa, Windows tarafında parmağın sonsuza kadar basılı kalmaması için otomatik `TOUCH_FLAG_UP / CANCEL` çağrıldığının birim testi.

#### Elle Test Adımları (Kullanıcı İçin Adım Adım)
1. PC'de **Windows Paint** (veya tarayıcıda çoklu dokunma test sayfası) açın.
2. Tabletten bağlantıyı kurun.
3. Tek parmağınızla tablet ekranında daireler çizin; Paint üzerinde kesintisiz çizildiğini görün.
4. İki parmağınızı aynı anda ekrana koyup paralel çizgiler çizin; çoklu dokunmanın (Multi-touch) çalıştığını doğrulayın.
5. İki parmağınızı birbirinden uzaklaştırarak Paint tuvalinin yakınlaştığını (Zoom) gözlemleyin.

#### Bilinen Riskler
- **Risk 1 (UIPI - Yönetici Pencerelerine Dokunamama):** Windows güvenlik mimarisi (User Interface Privilege Isolation) gereği, normal kullanıcı yetkisiyle çalışan bir program, "Yönetici Olarak Çalıştırılmış" (Administrator) bir pencereye veya Görev Yöneticisi'ne dokunma/fare girdisi gönderemez. *Çözüm:* PC uygulaması `app.manifest` dosyasında uygun yetki düzeyiyle çalıştırılacak ve kullanıcıya bu Windows kuralı açıklanacak.
- **Risk 2 (Takılı Kalan Dokunuş):** Paket kaybı veya bağlantı kopması anında parmak ekrandaysa Windows ekrana sürekli basılı tutulduğunu sanır. *Çözüm:* 500 ms boyunca aktif bir `DOWN/MOVE` durumu güncellenmezse veya bağlantı düşerse tüm aktif pointer'lar otomatik serbest bırakılacak (`CancelAllPointers`).

---

### Aşama 6: Sanal Monitör (IddCx Tabanlı Açık Kaynak Sürücü Entegrasyonu)

**Hedef:** PC ekranını sadece kopyalamak (Yansıt / Duplicate) yerine, Windows'a fiziksel olarak ikinci bir monitör takılmış gibi gösterip masaüstünü sağa/sola **Uzatmak (Extend)** ve sadece bu ikinci ekranı tablete göndermek.

> [!CAUTION]
> **Sürücü Güvenliği ve İmza Gerçeği:**
> Windows 10/11 çekirdeği ve User-Mode Driver Framework (UMDF), dijital olarak imzalanmamış ekran sürücülerinin yüklenmesini engeller. Sıfırdan C++ ile kendi sürücümüzü yazmak, Microsoft EV Code Signing sertifikası veya bilgisayarın "Test Signing" (güvenliği düşüren ve bazı oyunların anti-cheat sistemlerini bozan mod) moduna alınmasını gerektirir.
> Bu nedenle Aşama 6'da sıfırdan C++ sürücü yazmak yerine, topluluk tarafından geliştirilen ve imzalı dağıtımları bulunan açık kaynak **IddCx Sanal Ekran Sürücüsü** (ör. *Virtual-Display-Driver / MTT IddSampleDriver*) kullanılacak ve C# uygulamamız bu sanal monitörü yakalayacaktır.

#### Görev Listesi
1. **Sürücü Kurulum ve Doğrulama Rehberi:** Açık kaynak IddCx sürücüsünün kurulumu ve tablet çözünürlük/yenileme hızı (`1920x1200@60Hz` veya `2560x1600@60Hz`) yapılandırmasını hazırlamak.
2. **Çoklu Ekran Seçici (`DisplayEnumerator`):**
   - PC uygulamasında sistemdeki tüm ekranları (`\\.\DISPLAY1`, `\\.\DISPLAY2` vb.) listelemek ve sanal monitörü otomatik tanımak.
   - Ekran yakalayıcıyı (Aşama 1'deki modül) ana ekran yerine sanal monitörü yakalayacak şekilde yönlendirmek.
3. **Sanal Monitör Dokunma Eşlemesi:**
   - Aşama 5'teki `InjectTouchInput` koordinatlarını ana ekran (`0,0` – `1920,1080`) yerine ikinci ekranın masaüstü koordinatlarına (ör. `1920,0` – `3840,1080`) eşlemek.
4. **Bağlantı Koptuğunda Pencereleri Kurtarma:**
   - Tablet bağlantısı kesildiğinde pencerelerin görünmeyen sanal ekranda hapsolmasını önlemek için kullanıcıya tek tıkla sanal ekranı kapatma veya "Sadece 1. Ekran" moduna dönme (`SetDisplayConfig`) butonu sunmak.

#### "Bitti" Ölçütü
- Windows **Görüntü Ayarları (Display Settings)** altında 2 numaralı ekran görünür ve **"Bu ekranları genişlet (Extend these displays)"** seçilebilir.
- PC'de bir pencereyi ekranın sağ kenarından dışarı sürüklediğinizde tablet ekranından içeri girer.
- Tablette o pencereye dokunduğunuzda ana ekrandaki fare imleci veya pencereler etkilenmeden sanal ekrandaki pencere kontrol edilir.

#### Otomatik Test Edilebilenler
- Çoklu monitör sanal masaüstü (Virtual Desktop Bounding Box) koordinat hesaplayıcısının birim testleri (ör. 2. monitör solda, sağda veya üstte konumlandırıldığında negatif/pozitif piksel koordinatlarının `0..65535` sanal ekran uzayına doğru dönüştürülmesi).

#### Elle Test Adımları (Kullanıcı İçin Adım Adım)
1. Belirtilen açık kaynak IddCx sanal ekran sürücüsünü kurun ve Windows **Aygıt Yöneticisi > Görüntü Bağdaştırıcıları** altında sanal ekranın ünlem işareti olmadan çalıştığını görün.
2. Masaüstüne sağ tıklayıp **Görüntü Ayarları**'na girin; 1 ve 2 numaralı ekranları görüp **"Bu ekranları genişlet"** seçeneğini aktif edin.
3. PC gönderici uygulamasında **"Yakalanacak Ekran"** listesinden **"Ekran 2 (Sanal Monitör)"** seçeneğini seçin.
4. Tabletten bağlanın (USB, LAN veya Hotspot).
5. PC'de bir tarayıcı penceresini tutup sağa doğru ekran dışına sürükleyin; tablet ekranında belirdiğini görün ve tabletten dokunarak sayfayı kaydırın.

#### Bilinen Riskler ve Doğrulanmamış Noktalar
- **DOĞRULANMADI — Hibrit Dizüstü Bilgisayarda Sanal Sürücünün Hangi GPU'ya Bağlandığı:** Açık kaynak IddCx sürücüleri başlatıldığında varsayılan olarak hangi GPU adaptörüne bağlandığı dizüstü BIOS/MUX yapısına göre değişebilir. Eğer IddCx sürücüsü Intel iGPU'da oluşur ama bizim kodlayıcımız NVIDIA dGPU'da açılırsa, kareler GPU'lar arası kopyalanırken gecikme artabilir. Aşama 6'da sanal ekranın bağlı olduğu `LUID` (Locally Unique Identifier) okunarak kodlayıcı aynı GPU üzerinde başlatılacaktır.
- **DOĞRULANMADI — Sanal Monitörde "Fare İmleci Görünmeme" Sorunu:** Fiziksel fare bağlı olmadığında veya imleç sanal ekrana geçtiğinde bazı IddCx sürücüleri donanımsal fare imlecini (Hardware Cursor) çizmez. *Çözüm:* Ekran yakalayıcıda `IsCursorCaptureEnabled = true` açık tutulacak; eğer sürücü imleci gizliyorsa yakalama katmanında imleç ikonu yazılımsal olarak (Software Cursor Overlay) kareye eklenecektir.

