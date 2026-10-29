<p align="right">🌐 <b>ภาษาไทย</b> · <a href="README.en.md">English</a></p>

<div align="center">

# 🌾 Stardew MCP: Local AI Edition

**ให้ AI สองตัวเล่น Stardew Valley เองแบบยาว ๆ บนการ์ดจอในบ้าน ตั้งเป้า achievement ทุกอัน**

Mod สำหรับ [SMAPI](https://smapi.io) ที่เปิด [MCP](https://modelcontextprotocol.io) server *อยู่ในเกม*
พัฒนาต่อจาก [duckxy166/stardew-ai-agent](https://github.com/duckxy166/stardew-ai-agent) (ได้รับอนุญาตจากเจ้าของแล้ว)
เวอร์ชันนี้เน้นให้ **โมเดลเล็กในเครื่อง** (Qwen3.5 9B ผ่าน Ollama) เล่นได้หลายสิบวันในเกมโดยไม่ต้องมีคนเฝ้า

[![Latest release](https://img.shields.io/github/v/release/Pichaya31847/stardew-ai-agent?style=flat-square&label=release&color=4caf50)](https://github.com/Pichaya31847/stardew-ai-agent/releases/latest)
[![Upstream](https://img.shields.io/badge/upstream-duckxy166%2Fstardew--ai--agent-555?style=flat-square&logo=github)](https://github.com/duckxy166/stardew-ai-agent)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
![Stardew Valley 1.6+](https://img.shields.io/badge/Stardew_Valley-1.6+-7cb342?style=flat-square)
![SMAPI 4.0+](https://img.shields.io/badge/SMAPI-4.0+-5c6bc0?style=flat-square)
![Ollama](https://img.shields.io/badge/Ollama-stardew--local-000?style=flat-square)

</div>

## ที่มา

โปรเจกต์นี้ต่อยอดจาก **Stardew MCP** ของ [duckxy166](https://github.com/duckxy166/stardew-ai-agent) ซึ่งทำ mod ที่ให้
Claude, GPT, Gemini หรือโมเดลในเครื่อง เดิน ทำฟาร์ม ตกปลา คุยกับชาวเมือง และต่อรองกันเองในฟาร์มเดียวกัน
(ดูได้ในคลิป [EP.1 บน YouTube](https://youtu.be/Drd5lWh3kFI) ที่ AI สามตัวแย่งผักกันเอง) เราได้รับอนุญาตจากเจ้าของให้พัฒนาต่อ
โค้ดต้นฉบับอยู่ที่ branch [`main`](https://github.com/Pichaya31847/stardew-ai-agent/tree/main)
ส่วนงานของเวอร์ชันนี้อยู่ที่ branch `feature/new-AI`

ต้นฉบับทำงานดีมากกับโมเดลใหญ่ผ่าน API แต่พอใช้ Qwen 9B บนการ์ดจอ 12 GB แล้วปล่อยเล่นยาว ๆ จะเจอปัญหาหลายอย่าง
เช่น โมเดลสะกดชื่อชาวบ้านภาษาไทยผิดจนหาตัวไม่เจอ, ลงเหมืองแล้วหยุดตั้งแต่ชั้นแรกเพราะกระเป๋าเต็ม, ซื้อเมล็ดวันสุดท้ายของฤดู,
ไม่เคยสร้างเล้าหรือซื้อสัตว์ และ context เต็มจนต้องเริ่มบทสนทนาใหม่วันละหลายรอบ
เวอร์ชันนี้แก้จาก log การรันจริงหลายรอบ (ล่าสุด 62 วันในเกมต่อเนื่อง 19 ชั่วโมง)

## เปรียบเทียบกับต้นฉบับ

| เรื่อง | ต้นฉบับ (duckxy166, `main`) | เวอร์ชันนี้ |
|---|---|---|
| เป้าหมายระยะยาว | ไม่มี AI เล่นไปวัน ๆ ตามที่เห็น | `goals`: achievement ทุกอัน (ยกเว้น Joja) วัดจากข้อมูลเกม บอกอันที่ใกล้สำเร็จ และบันทึก `progress_pN.json` ทุกคืนว่าส่งขาย ทำอาหาร คราฟต์ ตกปลาอะไรไปแล้วบ้าง |
| เล่นด้วยโมเดลเล็กในเครื่อง | ใช้ได้ผ่าน `BaseUrl` แต่โมเดลเล็กงงกับเครื่องมือ 70 ตัว | `BrainTools: "compact"` (ส่งคำอธิบายเต็มเฉพาะเครื่องมือที่ใช้บ่อย), `BrainMemoryLines`, prompt สั้นลง และ [ชุดติดตั้ง `local/`](docs/local-ai.th.md) พร้อมโมเดล `stardew-local` ที่จูนแล้ว |
| จำนวนเครื่องมือ | 71 | 80: เพิ่ม `goals`, `mine`, `machine`, `fish_guide`, `upgrade_tool`, `upgrade_backpack`, `community_center`, `morning_chores`, `forage` |
| ภาษาที่ AI อ่าน | ตามภาษาเกม (เกมไทย = ชื่อไทย โมเดลเล็กสะกดผิดบ่อย) | ชื่อทุกอย่างเป็น key ภาษาอังกฤษเสมอ บทพูด จดหมาย และตัวเลือกในเมนูดึงฉบับอังกฤษตาม key แยกภาษาความคิด/คำพูด/งานได้ (`SpeechLanguage`, `WorkLanguage`) และตีกลับข้อความที่หลุดเป็นภาษาจีน |
| ลงเหมือง | เดินไปกดบันไดเองทีละชั้นด้วย `interact` | `mine(floors)`: นั่งลิฟต์ไปชั้นลึกสุด ทุบหินหาบันได สู้ เปิดหีบ เก็บควอตซ์และของบนพื้น ฝากของไว้ในหีบก่อนลง และกระเป๋าเต็มก็ลงต่อได้ |
| ตกปลา | ตกปลาได้ แต่ไม่รู้ว่าปลาอะไรอยู่ที่ไหน | `fish_guide`: ปลาทุกชนิดรวมปลาตำนาน บอกที่ ฤดู เวลา อากาศ และ `fish` เล็งตำแหน่งปลาตำนานให้ |
| จีโอดและพิพิธภัณฑ์ | ต้องเดินไปเองทุกขั้น | `crack_geode` เดินไปหา Clint และทุบทั้งกอง บอกว่าของไหนพิพิธภัณฑ์ยังไม่มี `museum` บริจาคทุกชิ้นที่รับได้และรับรางวัลให้เอง |
| เครื่องจักร | วางแล้วใส่ของเองทีละเครื่อง | `machine`: ดูทุกเครื่อง ใส่ของทุกเครื่องที่ว่าง และเก็บของ รองรับ mod [Automate](https://www.nexusmods.com/stardewvalley/mods/1063) ด้วย: หีบที่ติดเครื่องรับได้เฉพาะวัตถุดิบของเครื่องนั้น |
| อัปเกรด | ซื้อของที่ Clint ผ่านเมนูเอง | `upgrade_tool` สั่งและรับของคืนเอง, `upgrade_backpack` คำสั่งเดียว, แผนเก็บเงินสร้าง Coop → อัปเกรดบ้าน → Barn บอกว่ายังขาดเงิน ไม้ และหินเท่าไร |
| งานตอนเช้า | AI มักลืมเปิดกล่องของขวัญ อ่านจดหมาย หรือรับเงินเควสต์ | `morning_chores` ทำให้ก่อนเริ่มคิด และปลูกเมล็ดที่ยังโตทันในฤดู |
| หีบ | เป็นของใครของมัน หยิบของคนอื่นนับเป็นขโมย | ใช้ร่วมกันทุกใบ บันทึกทุกการใส่/หยิบ (`chests.jsonl`) `chest(action="all")` และ briefing ตอนเช้าบอกว่าหีบไหนมีอะไร ใครเพิ่งหยิบอะไรไป |
| ซื้อและขาย | ซื้อเมล็ดที่ปลูกไม่ทันได้ `ship` ต้องอยู่ในฟาร์มก่อน | `buy` ไม่ยอมซื้อเมล็ดที่จะตายก่อนเก็บเกี่ยว, `ship` เดินไปฟาร์มเอง, กันของที่ต้องใช้คราฟต์ไม่ให้ถูกขาย, เปิดเคาน์เตอร์ร้านให้เอง |
| เข้านอนเร็วเกินไป | ไม่มีตัวกัน | ไม่ยอมให้นอนก่อนเวลา พร้อมบอกงานถัดไปที่ทำได้จริง (นับเฉพาะพืชของตัวเอง) |
| ทำผิดซ้ำ ๆ | loop guard เฉพาะ action | เพิ่ม `RepeatBrake` สำหรับ AI ในตัว: เตือนเมื่อใช้เครื่องมือเดิมพลาดครั้งที่ 3 และไม่ให้ใช้เมื่อพลาดครั้งที่ 5 |
| เทศกาลแบบเล่นด้วยกัน | ติดหน้าจอรอคนอื่นพร้อม | รอ ready check สูงสุด 60 วินาที บอกคนอื่น แล้วยกเลิกถ้ายังไม่พร้อม และออกจากเทศกาลได้ |
| เงิน | กระเป๋ารวมถ้าเซฟตั้งไว้แบบนั้น | `SeparateWallets` สลับเป็นกระเป๋าแยกให้เองข้ามคืน |
| log และสถิติ | JSONL | บีบอัด gzip แบบไม่เสียข้อมูล, `stats_NNN.json` มีความคืบหน้าและเหตุการณ์, `progress_pN.json`, `chests.jsonl` |
| สั่งงานระหว่างรัน | ผ่าน MCP client | เพิ่ม `OperatorDashboard`, `POST /api/order` และคำสั่ง `mcp_order` สั่ง AI ในตัวได้ระหว่างเล่น |
| เริ่มฟาร์มใหม่ | กรอกหน้าสร้างตัวละครเอง | `PrefillNewGame` กรอกให้ (ชื่อ ฟาร์ม ข้ามฉากเปิด) |

**ที่ยังทำไม่ได้:** achievement ของเกมตู้ (Prairie King, Fector's Challenge), สาย Joja (ตั้งใจไม่ทำ), mod จาก Nexus ต้องโหลดเอง,
ชุดติดตั้ง `local/` รองรับแค่ Windows และฟีเจอร์ใหม่บางอย่างยังไม่มี smoke test (ดู [coverage](docs/coverage.md))

## จุดเด่น

- 🤖 **เล่นได้แทบทั้งเกมเอง** มีเครื่องมือ 80 ตัว ครอบคลุมทำฟาร์ม ตกปลา ลงเหมืองและต่อสู้ ทำอาหาร คราฟต์ของ เครื่องจักร ซื้อขาย
  เลี้ยงสัตว์ สร้างอาคาร งานเทศกาล เควสต์ Community Center พิพิธภัณฑ์ ความสัมพันธ์ การแต่งงานและมีลูก
  → [ดูทุกอย่างที่ทำได้](docs/features.th.md)
- 🏆 **มีเป้าหมาย**: achievement ทุกอัน วัดความคืบหน้าจากข้อมูลเกมจริง และบอกขั้นถัดไปที่ต้องเก็บเงินทำ
- 💻 **รันในบ้านได้ทั้งหมด**: สคริปต์ติดตั้งตัวเดียว + โมเดล `stardew-local` (Qwen3.5 9B ที่จูนแล้ว) ไม่ต้องจ่ายค่า API
- 👥 **AI สูงสุด 3 ตัวในฟาร์มเดียว** แบบจอแบ่ง (split-screen co-op) จะเล่นด้วยหรือไม่ก็ได้ เงินแยกกระเป๋า แปลงผักเป็นของใครของมัน หีบใช้ร่วมกันพร้อมบันทึก
- 🗣️ **ได้ยินแค่คนที่อยู่ใกล้** `say` ไปถึงเฉพาะคนที่จอเห็นคนพูด ส่วน `think` เป็นความคิดส่วนตัว (คนดูเห็น)
  และ AI ทุกตัวเห็นว่าคนอื่น *ทำอะไรอยู่จริง ๆ* เทียบกับที่พูดไว้
- ⏸️ **เวลาหยุดเดินระหว่างที่ AI คิด** โมเดลช้าก็ไม่เสียเวลาในเกม (ปิดได้ เวลาเราเล่นด้วย)
- 🧠 **ความจำข้ามวัน** ไดอารี่ บทเรียน แผน และบันทึก `self` ที่ AI แต่ละตัวเขียนใหม่ทุกเย็น
- 🎥 **ทำมาเพื่อสตรีมและทำคลิป** หน้า overlay ใส่ใน OBS ได้ หน้า replay ดูย้อนหลังรายวัน log ไดอารี่ และสถิติรายวัน
- 🔌 **ใช้ AI ตัวไหนก็ได้** ใช้ AI ในตัว mod (Anthropic, OpenAI, Gemini, OpenRouter, Ollama, LM Studio)
  ต่อกับ MCP client (Claude Code, Claude Desktop, Cursor...) หรือเรียกผ่าน HTTP ตรง ๆ

## ใช้ได้ 3 แบบ

| อยากทำอะไร | ใช้อะไร | ต้องมีอะไร |
|---|---|---|
| ดู AI เล่นฟาร์มของเรา หรือเล่นไปด้วยกัน | **AI ในตัว mod** ตั้งค่าในเมนู ไม่ต้องลงโปรแกรมอื่น | โมเดลในเครื่อง (ชุด [`local/`](docs/local-ai.th.md)) หรือ API key (Anthropic, OpenAI, Gemini) |
| สั่งงาน AI ของเราเอง ("รดน้ำผักแล้วไปหา Abigail") | **MCP client** ตัวไหนก็ได้ (Claude Code, Claude Desktop, Cursor...) ต่อที่ `http://localhost:8770/mcp` | client ตัวนั้น |
| เขียนบอท overlay สำหรับสตรีม หรือระบบอัตโนมัติของตัวเอง | **[HTTP API](docs/api.md)** (`/api/state`, `/api/control`, `/overlay`, `/replay`...) | ภาษาอะไรก็ได้ที่เรียก HTTP ได้ |

## ติดตั้ง

### แบบที่ 1: โมเดลในเครื่อง (แนะนำ)

ต้องมี Stardew Valley 1.6 (PC) และการ์ดจอ VRAM ประมาณ 10 GB โหลด repo นี้แล้วรัน

```bash
powershell -ExecutionPolicy Bypass -File local\setup.ps1
```

สคริปต์จะหาเกม ลง SMAPI ลง mod ลง Ollama และสร้างโมเดล `stardew-local` ให้ จากนั้นเปิดเกมด้วย `local\start.cmd`
รายละเอียดทั้งหมด: [เล่นด้วยโมเดลในเครื่อง](docs/local-ai.th.md)

### แบบที่ 2: ลง mod อย่างเดียว (ใช้ API key)

ต้องมี Stardew Valley 1.6 ขึ้นไป (PC) และ [SMAPI](https://smapi.io) 4.0 ขึ้นไป
ถ้าลง [Generic Mod Config Menu](https://www.nexusmods.com/stardewvalley/mods/5098) ด้วย จะตั้งค่าในเกมได้เลย (ไม่บังคับ)

1. ลง SMAPI แล้วเปิดเกมผ่าน SMAPI หนึ่งครั้ง
2. ดาวน์โหลดไฟล์ zip `StardewMCP` ตัวล่าสุดจากหน้า [Releases](https://github.com/Pichaya31847/stardew-ai-agent/releases)
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
   | `openai` + Ollama | `stardew-local` (จากชุด `local/`) | `BaseUrl`: `http://127.0.0.1:11434/v1` ไม่ต้องมี key |
   | `anthropic` | `claude-haiku-5-5` (ค่าเริ่มต้น), `claude-sonnet-5-5`, ... | [console.anthropic.com](https://console.anthropic.com) |
   | `openai` | `gpt-5-mini` (ค่าเริ่มต้น), `gpt-5`, ... | [platform.openai.com](https://platform.openai.com) หรือ server ที่รองรับแบบ OpenAI โดยใส่ `BaseUrl`: OpenRouter, LM Studio |
   | `gemini` | `gemini-3.8-flash` (ค่าเริ่มต้น), ... | [aistudio.google.com](https://aistudio.google.com) |
   | `none` | | ไม่ใช้ AI ในตัว ให้ MCP client เป็นคนขับที่นั่งนี้ |

3. โหลดเซฟไหนก็ได้ AI จะเริ่มเล่นเอง และมีแผงเล็ก ๆ บนจอบอกความคิด แชต และสิ่งที่กำลังทำ

```json
"Seats": [
  { "Role": "ai", "Brain": "openai", "Model": "stardew-local", "BaseUrl": "http://127.0.0.1:11434/v1" }
]
```

### หลาย AI หรือเราเล่นกับ AI

เปิด `SplitScreen` แล้วจะได้สูงสุด 4 จอในฟาร์มเดียว (แบ่ง 2×2 แบบในเกม) แต่ละที่นั่งเลือก `Role` ได้: `ai`, `human`
(เราเล่นเอง เฉพาะ P1), `camera` (จอไว้ดูอย่างเดียว) หรือ `off` ถ้าฟาร์มมีกระท่อมไม่พอ mod จะสร้างให้และพา farmhand
เข้าเกมเอง ไม่ต้องมีจอย ตัวอย่างที่ใช้รันจริง (AI สองตัวบนโมเดลในเครื่อง + จอกล้อง) อยู่ใน
[`local/config.example.json`](local/config.example.json)

`FreezeTimeWhileThinking` เปิด = ทั้งโลกหยุดตอน AI คิด โมเดลช้าก็ไม่เสียเวลาในเกม · ปิด = เวลาเดินต่อ เหมาะตอนเราเล่นด้วย
ค่าตั้งทั้งหมด: [Configuration](docs/configuration.md) (ภาษาอังกฤษ)

### ต่อกับ AI client ของเราเอง

เปิดเกม โหลดเซฟ แล้วเพิ่ม mod เป็น MCP server ตัวอย่างสำหรับ [Claude Code](https://claude.com/claude-code):

```bash
claude mcp add --transport http stardew http://localhost:8770/mcp
```

ถ้าเป็นจอแบ่ง farmer แต่ละคนมี endpoint ของตัวเอง: `/mcp/p1` (เหมือน `/mcp`), `/mcp/p2`, `/mcp/p3` และตั้งที่นั่งนั้นเป็น `"Brain": "none"`

### หยุดชั่วคราว

กด `F8` ในหน้าต่างเกม: ให้ action ที่ทำอยู่จบก่อน แล้วทั้งโลกจะหยุด กด `F8` อีกครั้งเพื่อเล่นต่อ
การสั่งหยุดหลังจบวันและคำสั่งอื่น ๆ: [HTTP API](docs/api.md#run-control)

## ควรรู้ก่อนใช้

- **ค่าใช้จ่าย** โมเดลในเครื่องไม่เสียเงิน แต่กินการ์ดจอเต็มที่ (RTX 5070 ใช้ไฟประมาณ 250 W, 72°C ตอนรัน) ถ้าใช้ API
  หนึ่งวันในเกมใช้ประมาณ 50–300k input tokens ต่อ AI ตั้ง `DailyTokenBudget` เพื่อให้หยุดเมื่อใช้ครบ
- **ภาษา** ตั้งเกมเป็นภาษาอังกฤษจะดีที่สุดสำหรับโมเดลเล็ก ส่วนความคิดและคำพูดให้คนดูอ่านเป็นไทยได้ด้วย `ThoughtLanguage` / `SpeechLanguage`
- **API key** ถูกเก็บเป็นข้อความธรรมดาใน `config.json` อย่าแชร์ไฟล์นี้
- **นิสัยการเล่น** มาจาก system prompt ที่ `Mods/StardewMCP/assets/system_prompt.md` แก้ไฟล์นี้เพื่อเปลี่ยนพฤติกรรมได้
- **Automate** (ไม่บังคับ) ให้ปิด `ShippingBin` ใน config ของ Automate ไม่งั้นหีบที่ติดกล่องขายจะถูกส่งขายหมด (`setup.ps1` ตั้งให้แล้ว)
- **ความแรงเครื่อง** 3–4 จอค่อนข้างหนัก บวกกับโมเดลในเครื่องควรใช้การ์ดจอระดับกลางขึ้นไป

## เอกสาร

| หน้า | มีอะไร |
|---|---|
| 💻 [โมเดลในเครื่อง](docs/local-ai.th.md) | ชุดติดตั้ง `local/`, โมเดล `stardew-local` ที่จูนแล้ว, การเผยแพร่โมเดล |
| 📋 [ทำอะไรได้บ้าง](docs/features.th.md) | ทุกความสามารถ แยกตามหมวด และสิ่งที่ยังทำไม่ได้ |
| 🧰 [เครื่องมือ](docs/tools.th.md) | เครื่องมือ MCP ทั้งหมดพร้อม argument แยกตามงาน |
| 🌐 [HTTP API](docs/api.md) (อังกฤษ) | endpoint, การควบคุมการรัน, ผลลัพธ์ของเครื่องมือและ reason code, ไฟล์ log |
| ⚙️ [Configuration](docs/configuration.md) (อังกฤษ) | ค่าตั้งทุกตัวใน `config.json`, ที่นั่งและ role, ภาษาของ overlay |
| 💰 [Economy](docs/economy.md) (อังกฤษ) | กระเป๋าเงิน ความเป็นเจ้าของ หีบที่ใช้ร่วมกัน |
| 🗺️ [Game coverage](docs/coverage.md) (อังกฤษ) | ระบบไหนของเกมเข้าถึงแล้ว ทดสอบแล้ว หรือยังไม่มี |
| 📖 [Glossary](CONTEXT.md) (อังกฤษ) | คำศัพท์ของโปรเจกต์: seat, brain, freeze, interrupt... |
| 🤝 [Contributing](CONTRIBUTING.md) (อังกฤษ) | แจ้งบั๊ก รันเทสต์ เพิ่มเครื่องมือ |

## แก้ปัญหา

| อาการ | ลองทำ |
|---|---|
| คอนโซล SMAPI ไม่ขึ้น `Agent MCP at ...` | เช็กว่าโฟลเดอร์คือ `Mods/StardewMCP/` (ไม่ได้ซ้อนสองชั้น) แล้วดู error ใน SMAPI log |
| AI ไม่เริ่มเล่น | ดูคอนโซล SMAPI หลังโหลดเซฟ ที่นั่งที่เริ่มไม่ได้จะขึ้น `pN: built-in AI not started: <สาเหตุ>` พิมพ์ `mcp_brain` เพื่อดู AI ที่รันอยู่ |
| `HTTP 500: XML syntax error` ใน log | Ollama อ่าน tool call ของ Qwen ไม่ออกเป็นบางครั้ง mod ลองใหม่เองอัตโนมัติ ไม่ต้องทำอะไร |
| เกมดูค้าง นาฬิกาไม่เดิน | น่าจะหยุดรอ AI คิดอยู่ `/api/state` บอกว่าใครกำลังคิด AI ที่เงียบนานเกิน `ThinkTimeoutSeconds` จะไม่ถือเวลาโลกไว้อีก |
| AI ทำ action เดิมที่พังซ้ำ ๆ | loop guard และ `RepeatBrake` จะใส่คำใบ้ แล้วบล็อกการทำซ้ำ (`RepeatHintAfter`, `RepeatBlockAfter`, `NoProgressAfter`) |
| พอร์ต 8770 ถูกใช้อยู่ | เปลี่ยน `HttpPort` แล้วใช้พอร์ตใหม่ใน URL ของ MCP client |
| farmhand ไม่เข้าเกมตอนจอแบ่ง | กดอนุญาตตอน Windows Firewall ถาม และเปิด `AutoBuildCabins` ไว้ |

## Build เอง

```bash
cd mod/StardewMCP
dotnet build -c Release
```

[ModBuildConfig](https://www.nuget.org/packages/Pathoschild.Stardew.ModBuildConfig) จะหาโฟลเดอร์เกมให้เอง คัดลอก mod
ไปไว้ใน `Mods/` และสร้างไฟล์ zip ไว้ใน `bin/` เทสต์ที่รันได้โดยไม่ต้องมีเกมอยู่ใน [CONTRIBUTING.md](CONTRIBUTING.md#tests)
(`local\setup.ps1 -BuildFromSource` ก็ build และติดตั้งให้ในคำสั่งเดียว)

## เครดิต

- **[duckxy166/stardew-ai-agent](https://github.com/duckxy166/stardew-ai-agent)** โดย Duckxy: ต้นฉบับของ mod นี้ทั้งหมด
  (MCP server ในเกม, จอแบ่ง, AI ในตัว, perception, overlay/replay และเครื่องมือ 70 ตัว) เวอร์ชันนี้พัฒนาต่อโดยได้รับอนุญาตแล้ว
  ติดตามซีรีส์ได้ที่ YouTube [@LDuckxy](https://www.youtube.com/@LDuckxy) และสนับสนุนผู้พัฒนาต้นฉบับผ่าน [GitHub Sponsors](https://github.com/sponsors/duckxy166)
- **[Hunter-Thompson/stardew-mcp](https://github.com/Hunter-Thompson/stardew-mcp)**: โปรเจกต์แรกที่เชื่อม Stardew Valley กับ MCP
  ซึ่งต้นฉบับ fork มาก่อนจะเขียนใหม่ทั้งหมด
- **[Automate](https://www.nexusmods.com/stardewvalley/mods/1063)** โดย Pathoschild: รองรับแบบเลือกได้ ไม่มีโค้ดของ Automate อยู่ใน repo นี้
- โมเดล **Qwen3.5** (Apache 2.0) ผ่าน **[Ollama](https://ollama.com)**

## สัญญาอนุญาต

[MIT](LICENSE) เหมือนต้นฉบับ ดาวน์โหลด ใช้ แก้ไข และนำไปพัฒนาต่อได้ฟรี แค่คงข้อความ license ไว้
Stardew Valley เป็นเครื่องหมายการค้าของ ConcernedApe โปรเจกต์นี้เป็น mod ที่แฟนทำขึ้นเอง ไม่เกี่ยวข้องกับ ConcernedApe
และไม่มีไฟล์ของเกมรวมอยู่
