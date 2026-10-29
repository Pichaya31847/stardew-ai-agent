# เล่นด้วยโมเดลในเครื่อง

← **ภาษาไทย** · [English](local-ai.md) · [← README](../README.md) · [Configuration](configuration.md)

ให้ AI สองตัวทำฟาร์มแบบแบ่งจอ โดยใช้โมเดลที่รันบนเครื่องตัวเอง (Ollama) ไม่ต้องมี API key และไม่เสียเงินรายวัน
ของที่ต้องใช้ทั้งหมดอยู่ในโฟลเดอร์ [`local/`](../local) คือ `setup.ps1` ที่รันครั้งเดียว กับ `start.cmd` สำหรับเปิดเกม ตอนนี้รองรับเฉพาะ Windows

## ต้องมีอะไรบ้าง

| | |
|---|---|
| Stardew Valley 1.6 | ซื้อเองจาก Steam หรือ GOG (ตัวเกมแจกต่อไม่ได้) |
| การ์ดจอ | VRAM ประมาณ 10 GB สำหรับโมเดล 9B ที่ context 48k (RTX 3080 / 4070 / 5070 ขึ้นไป) |
| พื้นที่ดิสก์ | ประมาณ 10 GB (ตัวโมเดล 6.5 GB) |
| mod จาก Nexus (ไม่บังคับ) | [Automate](https://www.nexusmods.com/stardewvalley/mods/1063) (ให้เครื่องจักรดึงของจากหีบข้างๆ) และ [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) Nexus ต้องสมัครสมาชิก (ฟรี) จึงต้องโหลดเอง |

ถ้าการ์ดจอไม่ถึง ใส่ key ของ Claude, GPT หรือ Gemini ใน `config.json` แทนได้ ([seats](configuration.md#seats-and-roles))

## ติดตั้ง

1. โหลด repo นี้ (Code → Download ZIP หรือ `git clone`) แล้วแตกไฟล์ไว้ที่ไหนก็ได้
2. เปิด PowerShell ในโฟลเดอร์นั้นแล้วรัน

   ```
   powershell -ExecutionPolicy Bypass -File local\setup.ps1
   ```

   สคริปต์จะทำให้ตามลำดับ:
   - หาโฟลเดอร์เกมเอง
   - ถ้ายังไม่มี SMAPI จะติดตั้งให้จาก GitHub ของ SMAPI
   - ติดตั้ง mod ลงใน `local\Mods` จาก release ล่าสุดบน GitHub (ถ้าใส่ `-BuildFromSource` จะ build จากโค้ดในเครื่องแทน ต้องมี .NET SDK)
   - ถ้ายังไม่มี Ollama จะติดตั้งให้ผ่าน winget
   - สร้างโมเดล `stardew-local`

   ขั้นไหนทำไปแล้วจะข้ามให้เอง เพิ่ม mod แล้วรันซ้ำได้เลย
3. (ไม่บังคับ) แตกไฟล์ Automate และ Generic Mod Config Menu ลงใน `local\Mods` แล้วรัน `setup.ps1` อีกรอบ สคริปต์จะตั้งค่า Automate ให้ด้วย โดยปิดการส่งขายอัตโนมัติจากกล่องขาย ไม่งั้นของในหีบที่วางติดกล่องขายจะถูกขายหมด
4. ถ้าอยากให้ AI พูดไทยในเกมที่ตั้งเป็นภาษาอังกฤษ ให้แตก [Stardew Valley - THAI](https://www.nexusmods.com/stardewvalley/mods/7052) และ mod ฟอนต์ของมันลงใน `local\Mods` แล้วรัน `setup.ps1` อีกรอบ สคริปต์จะติดตั้ง pack `ThaiFontInEnglish` ที่โหลดฟอนต์ไทยให้ ทั้งที่เกมยังเป็นภาษาอังกฤษ (ซึ่งดีที่สุดสำหรับโมเดล) ใน config ตัวอย่างตั้งให้ AI คิด พูด และเขียนไดอารี่เป็นไทยไว้แล้ว
5. เปิดเกมด้วย `local\start.cmd` จะเริ่มฟาร์มใหม่ (หน้าสร้างฟาร์มกรอกให้แล้ว) หรือโหลดเซฟก็ได้ ส่วน overlay เปิดที่ `http://localhost:8770/overlay`

ตัวเลือกของ `setup.ps1`:
- `-GamePath "D:\...\Stardew Valley"` ระบุโฟลเดอร์เกมเอง
- `-ModsPath` ระบุโฟลเดอร์ mod เอง
- `-BuildFromSource` build mod จากโค้ดในเครื่อง
- `-SkipOllama` ไม่ติดตั้ง Ollama (ใช้โมเดลผ่าน API อย่างเดียว)
- `-OllamaModel ชื่อ` โหลดโมเดลที่มีคนเผยแพร่ไว้แล้ว แทนการสร้างเอง

## โมเดล stardew-local

`stardew-local` คือ Qwen3.5 9B (สัญญาอนุญาต Apache 2.0) จากคลังของ Ollama ใช้น้ำหนักโมเดลตามต้นฉบับ แต่ปรับค่าตามที่จูนมาจากการรันยาวหลายรอบ ค่าที่จูนไว้ทั้งหมดอยู่ใน repo นี้สามที่:

| ที่ไหน | ค่าอะไร |
|---|---|
| [`local/Modelfile`](../local/Modelfile) | `num_ctx 49152`, `num_predict 2048`, `temperature 0.3`, `top_k 20`, `top_p 0.95`, `presence_penalty 1.5` |
| [`local/start.ps1`](../local/start.ps1) | ค่าเซิร์ฟเวอร์ Ollama: โหลดทีละโมเดล, flash attention, KV cache แบบ q8, context 48k |
| [`local/config.example.json`](../local/config.example.json) | ค่าของ mod: `BrainTools: "compact"`, `BrainMemoryLines: 20`, งานตอนเช้า, session break, `WorkLanguage: "English"` |

`setup.ps1` จึงสร้างโมเดลที่เหมือนกันทุกอย่างบนเครื่องไหนก็ได้ (`ollama pull qwen3.5:9b` แล้ว `ollama create stardew-local -f Modelfile`)

### เผยแพร่โมเดลบน ollama.com (ไม่บังคับ)

ถ้าอยากให้คนอื่น `ollama pull` ได้ในคำสั่งเดียว:

1. ล็อกอิน ollama.com
2. เพิ่ม public key ของ Ollama ในบัญชี (ไฟล์ `%USERPROFILE%\.ollama\id_ed25519.pub`)
3. รันคำสั่งนี้

   ```
   ollama cp stardew-local <ชื่อคุณ>/stardew-local
   ollama push <ชื่อคุณ>/stardew-local
   ```

4. คนอื่นรัน `setup.ps1 -OllamaModel <ชื่อคุณ>/stardew-local`

## ปล่อยเวอร์ชันของ mod (สำหรับคนดูแล repo)

`setup.ps1` โหลด zip จาก release ล่าสุดบน GitHub วิธีทำ zip:
1. รัน `dotnet build -c Release -p:EnableModDeploy=false` ใน `mod/StardewMCP` จะได้ไฟล์ `bin/Release/net6.0/StardewMCP x.y.z.zip`
2. สร้าง release ใหม่บน GitHub (Releases → Draft a new release) แล้วแนบ zip นั้น

## สิ่งที่ไม่แชร์

เซฟ, log และไฟล์ความจำของ AI (`lessons`, `plan`, `notes`, `self`, `people`) อยู่ในเครื่องของแต่ละคน ทุกคนเริ่มฟาร์มของตัวเอง และ AI จะสร้างความจำของตัวเองขึ้นมาใหม่ ส่วนไฟล์เกม, โค้ดเกมที่ decompile ออกมา และ mod ของคนอื่น ไม่ถูกใส่ใน repo นี้เด็ดขาด
