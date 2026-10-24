<div align="center">

# 🌾 Stardew MCP

**ให้ AI เล่น Stardew Valley เอง จะปล่อยเล่นคนเดียว เล่นกับเรา หรือให้ AI 3 ตัวทำฟาร์มบ้านเดียวกันก็ได้**

Mod สำหรับ [SMAPI](https://smapi.io) ที่เปิด [MCP](https://modelcontextprotocol.io) server *อยู่ในเกม*
ให้ Claude, GPT, Gemini หรือโมเดลในเครื่องเดิน ทำฟาร์ม ตกปลา คุยกับชาวเมือง ต่อรองกับเพื่อนร่วมฟาร์ม
และเข้านอนได้เองทั้งหมด

[![Latest release](https://img.shields.io/github/v/release/duckxy166/stardew-ai-agent?style=flat-square&label=release&color=4caf50)](https://github.com/duckxy166/stardew-ai-agent/releases/latest)
[![GitHub stars](https://img.shields.io/github/stars/duckxy166/stardew-ai-agent?style=flat-square&color=f5c518)](https://github.com/duckxy166/stardew-ai-agent/stargazers)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
![Stardew Valley 1.6+](https://img.shields.io/badge/Stardew_Valley-1.6+-7cb342?style=flat-square)
![SMAPI 4.0+](https://img.shields.io/badge/SMAPI-4.0+-5c6bc0?style=flat-square)
[![YouTube](https://img.shields.io/badge/YouTube-watch_EP.1-ff0000?style=flat-square&logo=youtube&logoColor=white)](https://youtu.be/Drd5lWh3kFI)

[English](README.md) · **ภาษาไทย**

<a href="https://youtu.be/Drd5lWh3kFI">
  <img src="docs/assets/ep01-cover.png" width="720" alt="ดูบน YouTube: ปล่อย Claude, GPT และ Gemini ทำฟาร์มด้วยกันใน Stardew Valley">
</a>

▶️ **[ดู EP.1 บน YouTube](https://youtu.be/Drd5lWh3kFI)**: ปล่อย Claude, GPT และ Gemini ทำฟาร์มด้วยกันใน Stardew Valley
แล้วมันแย่งผักกันเอง (43 นาที)

</div>

## ในคลิปเกิดอะไรขึ้น

AI 3 ตัวย้ายมาอยู่ฟาร์มเดียวกันแบบไม่รู้จักกันมาก่อน ไม่มีใครกำหนดว่าต้องเป็นใคร ต้องอยู่ทีมไหน หรือต้องช่วยกัน
แต่ละตัวเห็นแค่จอของตัวเอง ได้ยินแค่คนที่ยืนอยู่ใกล้ ๆ และเขียนไดอารี่ของตัวเองทุกคืน ใน 5 วันในเกม:

- 👋 พี่ฟ้า (Gemini) เดินทักคนทั้งเมืองตั้งแต่วันแรก ทั้งที่ไม่มีใครสั่ง
- 🪙 พี่ส้ม (Claude) ใช้เงินจนเหลือ 0 เพื่อซื้อเมล็ด 3 เม็ดสุดท้าย
- 💭 ความคิดของพี่เขียว (GPT) มีภาษาอื่นโผล่มากลางประโยค
- 🚪 ไปยืนรอหน้าร้านตั้งแต่เช้า แต่ร้านยังไม่เปิด สองวันติด
- 🥕 เช้าวันที่ 5 ผักสุกพร้อมกันทั้งแปลง *"จะได้ไม่เก็บทับกัน"* แล้วก็ก้มเก็บผักของเพื่อน:
  **Claude เก็บผักเพื่อนไป 14 ต้น GPT 11 ต้น Gemini 0**
- 🌱 พอแบ่งแปลงเสร็จ เหลือหนึ่งตัวที่ไม่มีแปลงเลย

ไม่มีบทเลยสักฉาก mod บันทึกทุกการกระทำ ทุกความคิด และทุกประโยคที่คุยกัน เรื่องทั้งหมดในคลิปมาจาก log

## จุดเด่น

- 🤖 **เล่นได้แทบทั้งเกมเอง** มีเครื่องมือ 70 ตัว ครอบคลุมทำฟาร์ม ตกปลา ลงเหมืองและต่อสู้ ทำอาหาร คราฟต์ของ ซื้อขาย
  เลี้ยงสัตว์ สร้างอาคาร งานเทศกาล เควสต์ Community Center พิพิธภัณฑ์ ความสัมพันธ์และการแต่งงาน
  → [ดูทุกอย่างที่ทำได้](docs/features.th.md)
- 👥 **AI สูงสุด 3 ตัวในฟาร์มเดียว** แบบจอแบ่ง (split-screen co-op) จะเล่นด้วยหรือไม่ก็ได้ เงินแยกกระเป๋า แปลงผักและหีบเป็นของใครของมัน
  และ mod รู้ว่าใครไปหยิบของที่ไม่ใช่ของตัวเอง
- 🗣️ **ได้ยินแค่คนที่อยู่ใกล้** `say` ไปถึงเฉพาะคนที่จอเห็นคนพูด ส่วน `think` เป็นความคิดส่วนตัว (คนดูเห็น) การวางแผน การต่อรอง
  และการโกหกจึงเกิดแบบเจอหน้ากัน และจับโกหกได้ เพราะ AI ทุกตัวเห็นว่าคนอื่น *ทำอะไรอยู่จริง ๆ* เทียบกับที่พูดไว้
- ⏸️ **เวลาหยุดเดินระหว่างที่ AI คิด** โมเดลช้าก็ไม่เสียเวลาในเกม (ปิดได้ เวลาเราเล่นด้วย)
- 🧠 **ความจำข้ามวัน** ไดอารี่ บทเรียน แผน และบันทึก `self` ที่ AI แต่ละตัวเขียนใหม่ทุกเย็น นิสัยของแต่ละตัวเกิดจากสิ่งที่เจอในเกม
  ไม่ได้มาจากบท
- 🎥 **ทำมาเพื่อสตรีมและทำคลิป** มีหน้า overlay ใส่ใน OBS ได้ หน้า replay ดูย้อนหลังรายวัน log แบบ JSONL ไดอารี่ และสถิติรายวัน
- 🔌 **ใช้ AI ตัวไหนก็ได้** ใช้ AI ในตัว mod ด้วย API key ของคุณเอง (Anthropic, OpenAI, Gemini, OpenRouter, Ollama,
  LM Studio) ต่อกับ MCP client (Claude Code, Claude Desktop, Cursor...) หรือเรียกผ่าน HTTP ตรง ๆ

## ใช้ได้ 3 แบบ

| อยากทำอะไร | ใช้อะไร | ต้องมีอะไร |
|---|---|---|
| ดู AI เล่นฟาร์มของเรา หรือเล่นไปด้วยกัน | **AI ในตัว mod** ตั้งค่าในเมนู ไม่ต้องลงโปรแกรมอื่น | API key (Anthropic, OpenAI, Gemini) หรือโมเดลในเครื่อง (Ollama, LM Studio) |
| สั่งงาน AI ของเราเอง ("รดน้ำผักแล้วไปหา Abigail") | **MCP client** ตัวไหนก็ได้ (Claude Code, Claude Desktop, Cursor...) ต่อที่ `http://localhost:8770/mcp` | client ตัวนั้น |
| เขียนบอท overlay สำหรับสตรีม หรือระบบอัตโนมัติของตัวเอง | **[HTTP API](docs/api.md)** (`/api/state`, `/api/control`, `/overlay`, `/replay`...) | ภาษาอะไรก็ได้ที่เรียก HTTP ได้ |

## ติดตั้ง

ต้องมี Stardew Valley 1.6 ขึ้นไป (PC) และ [SMAPI](https://smapi.io) 4.0 ขึ้นไป
ถ้าลง [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) ด้วย จะตั้งค่าในเกมได้เลย (ไม่บังคับ)

1. ลง SMAPI แล้วเปิดเกมผ่าน SMAPI หนึ่งครั้ง
2. ดาวน์โหลดไฟล์ zip `StardewMCP` ตัวล่าสุดจากหน้า [Releases](https://github.com/duckxy166/stardew-ai-agent/releases)
3. แตกไฟล์ไว้ใน `Stardew Valley/Mods/` ให้ได้โฟลเดอร์ `Mods/StardewMCP/`
4. เปิดเกมผ่าน SMAPI คอนโซลของ SMAPI ควรขึ้น `Agent MCP at http://localhost:8770/mcp`

เปิดครั้งแรก mod จะสร้างไฟล์ `Mods/StardewMCP/config.json` ถ้ามี Generic Mod Config Menu ก็ตั้งค่าในเกมได้:
หน้าไตเติล → กดรูปฟันเฟือง → *Stardew MCP*

## เริ่มใช้งาน

### ให้ AI เล่นเอง

1. เปิดหน้าตั้งค่าของ mod (ในเกม หรือแก้ `config.json`)
2. ใส่ผู้ให้บริการ (`Brain`) โมเดล และ API key ให้ที่นั่ง **P1**:

   | `Brain` | โมเดล | key / ที่อยู่ server |
   |---|---|---|
   | `anthropic` | `claude-haiku-5-5` (ค่าเริ่มต้น), `claude-sonnet-5-5`, ... | [console.anthropic.com](https://console.anthropic.com) |
   | `openai` | `gpt-5-mini` (ค่าเริ่มต้น), `gpt-5`, ... | [platform.openai.com](https://platform.openai.com) หรือ server ที่รองรับแบบ OpenAI โดยใส่ `BaseUrl`: OpenRouter (`https://openrouter.ai/api/v1`), Ollama (`http://localhost:11434/v1` ไม่ต้องมี key), LM Studio |
   | `gemini` | `gemini-3.8-flash` (ค่าเริ่มต้น), ... | [aistudio.google.com](https://aistudio.google.com) |
   | `none` | | ไม่ใช้ AI ในตัว ให้ MCP client เป็นคนขับที่นั่งนี้ |

3. โหลดเซฟไหนก็ได้ AI จะเริ่มเล่นเอง และมีแผงเล็ก ๆ บนจอบอกความคิด แชต และสิ่งที่กำลังทำ

```json
"Seats": [
  { "Role": "ai", "Brain": "anthropic", "ApiKey": "sk-ant-..." }
]
```

### หลาย AI หรือเราเล่นกับ AI

เปิด `SplitScreen` แล้วจะได้สูงสุด 4 จอในฟาร์มเดียว (แบ่ง 2×2 แบบในเกม) แต่ละที่นั่งเลือก `Role` ได้: `ai`, `human`
(เราเล่นเอง เฉพาะ P1), `camera` (จอไว้ดูอย่างเดียว) หรือ `off` ถ้าฟาร์มมีกระท่อมไม่พอ mod จะสร้างให้และพา farmhand
เข้าเกมเอง ไม่ต้องมีจอย

ตัวอย่าง "เราเล่น P1 มี AI สองตัวเล่นด้วย และเวลาเดินตลอด":

```json
"SplitScreen": true,
"FreezeTimeWhileThinking": false,
"Seats": [
  { "Role": "human" },
  { "Role": "ai", "Brain": "anthropic", "ApiKey": "sk-ant-...", "Name": "Claude" },
  { "Role": "ai", "Brain": "gemini", "ApiKey": "AIza...", "Name": "Gemini" },
  { "Role": "off" }
]
```

`FreezeTimeWhileThinking` เปิด = ทั้งโลกหยุดตอน AI คิด โมเดลช้าก็ไม่เสียเวลาในเกม · ปิด = เวลาเดินต่อ เหมาะตอนเราเล่นด้วย
ค่าตั้งทั้งหมด: [Configuration](docs/configuration.md) (ภาษาอังกฤษ)

### ต่อกับ AI client ของเราเอง

เปิดเกม โหลดเซฟ แล้วเพิ่ม mod เป็น MCP server ตัวอย่างสำหรับ [Claude Code](https://claude.com/claude-code):

```bash
claude mcp add --transport http stardew http://localhost:8770/mcp
```

แล้วลองสั่ง *"รดน้ำผักให้หน่อย แล้วไปคุยกับ Abigail"* client ไหนที่ใช้ MCP แบบ streamable HTTP ได้ก็ใช้ URL เดียวกัน
ถ้าเป็นจอแบ่ง farmer แต่ละคนมี endpoint ของตัวเอง: `/mcp/p1` (เหมือน `/mcp`), `/mcp/p2`, `/mcp/p3` และตั้งที่นั่งนั้นเป็น
`"Brain": "none"`

### หยุดชั่วคราว

กด `F8` ในหน้าต่างเกม: ให้ action ที่ทำอยู่จบก่อน แล้วทั้งโลกจะหยุด กด `F8` อีกครั้งเพื่อเล่นต่อ
การสั่งหยุดหลังจบวันและคำสั่งอื่น ๆ: [HTTP API](docs/api.md#run-control)

## ควรรู้ก่อนใช้

- **ค่าใช้จ่าย** ทุก action คือการเรียกโมเดลหนึ่งครั้งพร้อมสถานะเกม หนึ่งวันในเกมใช้ประมาณ 50–300k input tokens ต่อ AI
  หนึ่งตัว แนะนำโมเดลราคาถูก (Haiku, Flash, GPT mini หรือโมเดลในเครื่อง) ตั้ง `DailyTokenBudget` เพื่อให้ที่นั่งหยุดเมื่อใช้ครบ
- **API key** ถูกเก็บเป็นข้อความธรรมดาใน `config.json` อย่าแชร์ไฟล์นี้
- **ภาษา** AI คิดและแชตตามภาษาของเกม ตั้ง `ThoughtLanguage` (เช่น `"Thai"` หรือ `"English"`) เพื่อบังคับภาษาได้
- **โมเดลในเครื่อง** ใช้ผ่าน `openai` + `BaseUrl` ได้ แต่โมเดลเล็กจะงงกับเครื่องมือ 70 ตัว
- **นิสัยการเล่น** มาจาก system prompt ที่ `Mods/StardewMCP/assets/system_prompt.md` แก้ไฟล์นี้เพื่อเปลี่ยนพฤติกรรมได้
- **ความแรงเครื่อง** 3–4 จอค่อนข้างหนัก หน้าต่าง 2560×1440 ที่ 60 FPS ต้องใช้การ์ดจอระดับกลาง

## เอกสาร

| หน้า | มีอะไร |
|---|---|
| 📋 [ทำอะไรได้บ้าง](docs/features.th.md) | ทุกความสามารถ แยกตามหมวด และสิ่งที่ยังทำไม่ได้ |
| 🧰 [เครื่องมือ](docs/tools.th.md) | เครื่องมือ MCP ทั้งหมดพร้อม argument แยกตามงาน |
| 🌐 [HTTP API](docs/api.md) (อังกฤษ) | endpoint, การควบคุมการรัน, ผลลัพธ์ของเครื่องมือและ reason code |
| ⚙️ [Configuration](docs/configuration.md) (อังกฤษ) | ค่าตั้งทุกตัวใน `config.json`, ที่นั่งและ role, ภาษาของ overlay |
| 🗺️ [Game coverage](docs/coverage.md) (อังกฤษ) | ระบบไหนของเกมเข้าถึงแล้ว ทดสอบแล้ว หรือยังไม่มี |
| 📖 [Glossary](CONTEXT.md) (อังกฤษ) | คำศัพท์ของโปรเจกต์: seat, brain, freeze, interrupt... |
| 🤝 [Contributing](CONTRIBUTING.md) (อังกฤษ) | แจ้งบั๊ก รันเทสต์ เพิ่มเครื่องมือ |

## แก้ปัญหา

| อาการ | ลองทำ |
|---|---|
| คอนโซล SMAPI ไม่ขึ้น `Agent MCP at ...` | เช็กว่าโฟลเดอร์คือ `Mods/StardewMCP/` (ไม่ได้ซ้อนสองชั้น) แล้วดู error ใน SMAPI log |
| AI ไม่เริ่มเล่น | ดูคอนโซล SMAPI หลังโหลดเซฟ ที่นั่งที่เริ่มไม่ได้จะขึ้น `pN: built-in AI not started: <สาเหตุ>` (ไม่มี key, ชื่อผู้ให้บริการผิด...) พิมพ์ `mcp_brain` เพื่อดู AI ที่รันอยู่ |
| พอร์ต 8770 ถูกใช้อยู่ | เปลี่ยน `HttpPort` แล้วใช้พอร์ตใหม่ใน URL ของ MCP client |
| เกมดูค้าง นาฬิกาไม่เดิน | น่าจะหยุดรอ AI คิดอยู่ `/api/state` บอกว่าใครกำลังคิด AI ที่เงียบนานเกิน `ThinkTimeoutSeconds` จะไม่ถือเวลาโลกไว้อีก |
| AI ทำ action เดิมที่พังซ้ำ ๆ | loop guard จะใส่คำใบ้ และสุดท้ายจะบล็อกการทำซ้ำ (`RepeatHintAfter`, `RepeatBlockAfter`, `NoProgressAfter`) |
| farmhand ไม่เข้าเกมตอนจอแบ่ง | กดอนุญาตตอน Windows Firewall ถาม และเปิด `AutoBuildCabins` ไว้ (หรือสร้างกระท่อมให้พอเอง) |

แจ้งบั๊กได้ที่ issue พร้อมลิงก์ SMAPI log (วิธีอยู่ใน [CONTRIBUTING.md](CONTRIBUTING.md#reporting-a-bug)) ช่วยได้มาก

## ⭐ ชอบไหม?

ถ้าดู AI สามตัวแย่งหัวผักกาดกันแล้วยิ้มได้ **ฝากกด Star ให้ repo นี้หน่อย** ช่วยให้คนเล่นเกมและสาย AI คนอื่นเจอโปรเจกต์นี้
และเป็นสัญญาณว่าตอนต่อไปควรทำต่อ

<a href="https://star-history.com/#duckxy166/stardew-ai-agent&Date">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=duckxy166/stardew-ai-agent&type=Date&theme=dark">
    <img alt="Star history" src="https://api.star-history.com/svg?repos=duckxy166/stardew-ai-agent&type=Date" width="600">
  </picture>
</a>

ช่วยกันได้อีกหลายทาง:

- 📺 ติดตามซีรีส์บน YouTube: [@LDuckxy](https://www.youtube.com/@LDuckxy)
- 💖 สนับสนุนผู้พัฒนาผ่าน [GitHub Sponsors](https://github.com/sponsors/duckxy166)
- 🐛 แจ้งบั๊กหรือส่ง pull request: [CONTRIBUTING.md](CONTRIBUTING.md)

## Build เอง

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) จะหาโฟลเดอร์เกมให้เอง คัดลอก mod
ไปไว้ใน `Mods/` และสร้างไฟล์ zip ไว้ใน `bin/` เทสต์ที่รันได้โดยไม่ต้องมีเกมอยู่ใน [CONTRIBUTING.md](CONTRIBUTING.md#tests)

สคริปต์ที่ให้ AI เล่นหลายวันต่อเนื่องแบบไม่ต้องเฝ้า (ความจำข้ามวัน สำรองเซฟ ตัวเฝ้าเกมแครช อัด OBS) ไม่ได้อยู่ใน repo นี้
ทุกอย่างที่สคริปต์แบบนั้นต้องใช้อยู่ใน [HTTP API](docs/api.md) และ [docs/recording.md](docs/recording.md) เป็นตัวอย่างการตั้งค่าอัดคลิปหนึ่งแบบ

## เครดิต

เริ่มต้นจากการ fork [Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp) ซึ่งเชื่อม Stardew Valley
กับ MCP ผ่าน Go server และ mod แบบ WebSocket เป็นคนแรก ตอนนี้โปรเจกต์ถูกเขียนใหม่ทั้งหมดเป็น mod ที่มี MCP server ในตัว
และเอาโค้ด Go server กับ WebSocket เดิมออกไปแล้ว

## สัญญาอนุญาต

[MIT](LICENSE) ดาวน์โหลด ใช้ แก้ไข และนำไปพัฒนาต่อได้ฟรี แค่คงข้อความ license ไว้
Stardew Valley เป็นเครื่องหมายการค้าของ ConcernedApe โปรเจกต์นี้เป็น mod ที่แฟนทำขึ้นเอง ไม่เกี่ยวข้องกับ ConcernedApe
และไม่มีไฟล์ของเกมรวมอยู่
