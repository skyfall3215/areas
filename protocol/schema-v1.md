# İkinci Ekran Projesi — Protokol v1 Şeması ve Test Vektörleri (`schema-v1.md`)

Bu doküman, PROTOCOL.md şartnamesine dayanan v1 protokol sabitlerini ve birim testler için referans alınan ikili (binary) test vektörlerinin tanımlarını içerir.

## 1. Protokol Sabitleri

| Sabit Adı | Tip | Değer | Açıklama |
| :--- | :--- | :--- | :--- |
| `MAGIC_0` | `u8` | `0x53` | 'S' |
| `MAGIC_1` | `u8` | `0x53` | 'S' |
| `VERSION_1` | `u8` | `0x01` | Sürüm 1 |
| `HEADER_SIZE` | `int` | `16` | Ortak Zarf Başlığı bayt boyutu |
| `DEFAULT_PORT` | `int` | `27183` | TCP veri ve kontrol portu |
| `DISCOVERY_PORT` | `int` | `27184` | UDP duyuru / keşif portu |
| `MAX_PAYLOAD_SIZE`| `int` | `4194304` | 4 MB maksimum gövde boyutu |

### Mesaj Tipleri (`MsgType`)
- `0x01` : `HELLO` (Tablet -> PC)
- `0x02` : `HELLO_ACK` (PC -> Tablet)
- `0x10` : `VIDEO_CONFIG` (PC -> Tablet, SPS + PPS)
- `0x11` : `VIDEO_FRAME` (PC -> Tablet, H.264 NAL)
- `0x12` : `KEYFRAME_REQ` (Tablet -> PC)
- `0x20` : `TOUCH_EVENT` (Tablet -> PC)
- `0x30` : `PING` (Çift yönlü)
- `0x31` : `PONG` (Çift yönlü)
- `0xF0` : `ERROR` (Çift yönlü)
- `0xFF` : `DISCONNECT` (Çift yönlü)

### Video Çerçeve Bayrakları (`Header.Flags`)
- `0x01` : `FLAG_KEYFRAME` (IDR / Anahtar Kare)
- `0x02` : `FLAG_CURSOR_EMBEDDED` (İmleç görüntüye gömülü)

---

## 2. Test Vektörleri Özeti

Aşağıdaki ikili dosyalar `protocol/test-vectors/` altında yer almaktadır ve hem C# hem Kotlin birim testleri tarafından okunup doğrulanır:

1. `hello_sample.bin` (48 bayt):
   - Header: Magic="SS", Version=1, MsgType=HELLO, Flags=0, Seq=1, TimestampUs=1000000, Length=32
   - Payload: NativeWidth=2560, NativeHeight=1600, MaxFps=60, PreferredBitrateKbps=15000, FeatureFlags=0x03, DeviceName="Galaxy Tab S9"

2. `hello_ack_sample.bin` (32 bayt):
   - Header: Magic="SS", Version=1, MsgType=HELLO_ACK, Flags=0, Seq=1, TimestampUs=1005000, Length=16
   - Payload: Status=0 (OK), CodecId=1 (H264), StreamWidth=1920, StreamHeight=1200, TargetFps=60, BitrateBps=15000000, SessionId=0x12345678

3. `video_config_sample.bin` (37 bayt):
   - Header: Magic="SS", Version=1, MsgType=VIDEO_CONFIG, Flags=0, Seq=2, TimestampUs=1010000, Length=21
   - Payload: SPS NAL (`00 00 00 01 67 42 C0 1E D9 00 A0 7B 40`) + PPS NAL (`00 00 00 01 68 CE 38 80`)

4. `video_frame_sample.bin` (34 bayt):
   - Header: Magic="SS", Version=1, MsgType=VIDEO_FRAME, Flags=1 (Keyframe), Seq=3, TimestampUs=1033333, Length=18
   - Payload: FrameNumber=1, CaptureDurationUs=2500, EncodeDurationUs=4200, NalData (`00 00 00 01 65 88 84 00 ...`)
