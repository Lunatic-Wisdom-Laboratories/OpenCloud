# My_dma_start0 (VU1)

One-time DMA chain that `mgInit` sends before the VU1 setup microprogram is uploaded. It contains no micro code. It sets the VIF cycle and mask state, then sends one GIF packet (a DIRECT block with a GIF tag and eight packed A+D writes, `My_DrawEnv`) that loads the initial GS drawing state: colour clamp, scissor, primitive mode control, texture alpha, texture 1 state, fog colour, texture 0 state (context 1), and alpha blending (context 1). It ends with DMA END, so it is sent with `sceDmaSend`, not as a CALL.

Notation: `0x0036xxxx` = EE address of the chain (symbol `My_dma_start0` at `main.symbols.txt:8971`; `My_DrawEnv` at `0x003634D0`). Listing: `port/build/vu1/dma_start0.txt`. Source of the words: `ps2/asm/pal/data/main/vudata.data.s:9-22`.

---

## 1. Role and entry points

| Item | Where | Notes |
|---|---|---|
| Send | `ps2/src/mglib.cpp:501`: `sceDmaSend(DmaCH1, My_dma_start0)`, then `sceGsSyncPath(0, 0)` (`:502`) | Only use in the repo (`mglib.hpp:55` declares it). |
| Before it | `sceGsResetGraph` (`mglib.cpp:453`), `sceGsSetDefDBuff` (`:465`), C-side caches (`:484-492`, no GS packet), `FlushCache` (`:500`) | Reset and default double-buffer state, then this chain. |
| After it | `sceDmaSend(DmaCH1, Vu_progmain)` (`:504`), `mgGetVuProgPacket(MG_VU_PROG_MAIN)` upload (`:507-509`), `sceGsSwapDBuff(&mgDBuff, 0)` (`:521`) | Section 8 explains which of these may overwrite the chain's registers. |
| Re-send | None. | Init only. No MSCAL reaches it. |
| Chain end | DMA END tag 0x70000000 at 0x00363550 | Not a RET (no caller). |

---

## 2. Register conventions

No VU registers are touched. The chain writes GS registers only, through the VIF1 DIRECT path to the GIF. Context-1 registers are TEX0_1, TEX1_1, ALPHA_1, SCISSOR_1; context-independent: COLCLAMP, PRMODECONT, TEXA, FOGCOL. Context-2 registers are not written by this chain.

VIF state set by the chain (inferred from the VIF encodings):

| Command | Word | Meaning |
|---|---|---|
| STCYCL | 0x01000404 | CL = 4, WL = 4 (write cycle with no skip). Affects UNPACK only; irrelevant here. |
| STMASK | 0x20000000 with mask data word 0x00000000 | Mask register = 0. |
| STMOD | 0x05000000 | Mode 0 (no accumulation). With STMOD 0 the mask has no effect on the following DIRECT, so the STMASK/STMOD words change nothing for this chain. |

---

## 3. Data memory (chain layout)

Chain addresses are EE addresses (16-byte qwords). "Words" are the four 32-bit words of each qword, low to high.

| EE address | Words | Decoding |
|---|---|---|
| 0x00363480 | 0x10000001, 0, 0, 0 | DMA CNT, qwc 1 (the next qword follows) |
| 0x00363490 | 0x01000404, 0x20000000, 0x00000000, 0x05000000 | VIF: STCYCL (CL 4, WL 4); STMASK (mask word 0x00000000, next word); STMOD 0 |
| 0x003634A0 | 0x1000000A, 0, 0, 0 | DMA CNT, qwc 10 (the next 10 qwords follow) |
| 0x003634B0 | 0, 0, 0, 0x50000009 | Word 3: VIF DIRECT, 9 qwords follow. Words 0-2: VIF NOP |
| 0x003634C0 | 0x00008008, 0x10000000, 0x0000000E, 0 | GIF tag: NLOOP 8, EOP 1, PRE 0, PRIM unused, FLG 0 (PACKED), NREG 1, REGS = A+D |
| 0x003634D0 | data 0x1 ; addr 0x46 | A+D: COLCLAMP |
| 0x003634E0 | data 0x00DF0000027F0000 ; addr 0x40 | A+D: SCISSOR_1 |
| 0x003634F0 | data 0x1 ; addr 0x1A | A+D: PRMODECONT |
| 0x00363500 | data 0x0000008000008080 ; addr 0x3B | A+D: TEXA |
| 0x00363510 | data 0x61 ; addr 0x14 | A+D: TEX1_1 |
| 0x00363520 | data 0x00C8C8C8 ; addr 0x3D | A+D: FOGCOL |
| 0x00363530 | data 0x20153005DD412580 ; addr 0x06 | A+D: TEX0_1 |
| 0x00363540 | data 0x44 ; addr 0x42 | A+D: ALPHA_1 |
| 0x00363550 | 0x70000000, 0, 0, 0 | DMA END |

Byte layout of each A+D qword: data in the low 64 bits, register address in the low byte of the high 64 bits (`dma_start0.txt` lines for `My_DrawEnv`, GIF tag word `0x8008`). Eight A+D qwords plus the GIF tag = the nine qwords of DIRECT 9.

---

## 4. Control flow

Linear. No branches, no loops. The chain is: DMA CNT (1 qw) -> VIF (STCYCL, STMASK, STMOD) -> DMA CNT (10 qw) -> VIF DIRECT 9 -> GIF tag -> eight A+D pairs -> DMA END.

The GIF tag has NLOOP 8 with NREG 1 and REGS = A+D. The GIF consumes one (data, address) qword per loop, so each of the eight pairs is one register write in the order of section 3.

---

## 5. Decoded GS register table

Raw values are the 64-bit data words of the A+D qwords. Field layouts are from `ps2/include/sce/libgraph.h` (`sceGsScissor` l.158-171, `sceGsAlpha` l.222-242, `sceGsTex1` l.357-369, `sceGsTexa` l.245-251, `sceGsTex0` l.253-...). Decodes were computed with a script, not by hand.

| GS register (addr) | Raw | Field decode | Meaning |
|---|---|---|---|
| COLCLAMP (0x46) | 0x1 | CLAMP (bit 0) = 1 | Colour results are clamped to 0-255. Context-independent. |
| SCISSOR_1 (0x40) | 0x00DF0000027F0000 | SCAX0 (bits 0-10) = 0; SCAX1 (bits 16-26) = 639 (0x27F); SCAY0 (bits 32-42) = 0; SCAY1 (bits 48-58) = 223 (0xDF) | Pixel scissor x 0..639, y 0..223, bounds inclusive (inferred from the GS scissor definition). |
| PRMODECONT (0x1A) | 0x1 | AC (bit 0) = 1 | Primitive attributes come from the PRIM register, not PRMODE. Context-independent. |
| TEXA (0x3B) | 0x0000008000008080 | TA0 (bits 0-7) = 0x80 (128); AEM (bit 15) = 1; TA1 (bits 32-39) = 0x80 (128) | Alpha for 16-bit texels: TA0 / TA1 are the alpha values; AEM = 1 gives transparent black where a texel's RGB is 0 (inferred from the GS manual). Context-independent. |
| TEX1_1 (0x14) | 0x61 | LCM (bit 0) = 1 (LOD fixed, K/L used); MXL (bits 2-4) = 0; MMAG (bit 5) = 1 (linear); MMIN (bits 6-8) = 1 (linear, no mipmap); MTBA (bit 9) = 0; L (bits 19-20) = 0; K (bits 32-43) = 0 | Texture filtering: bilinear magnification and minification, no mipmapping. Context 1. |
| FOGCOL (0x3D) | 0x00C8C8C8 | FCR (bits 0-7) = 200; FCG (bits 8-15) = 200; FCB (bits 16-23) = 200 | Fog colour (200, 200, 200). Context-independent. |
| TEX0_1 (0x06) | 0x20153005DD412580 | TBP0 (bits 0-13) = 0x2580 (9600); TBW (bits 14-19) = 4 (stride 4 x 64 = 256 texels); PSM (bits 20-25) = 0x14 (PSMT4, 4-bit indexed); TW (bits 26-29) = 7 (128 texels); TH (bits 30-33) = 7 (128 texels); TCC (bit 34) = 1 (RGBA from the CLUT); TFX (bits 35-36) = 0 (MODULATE); CBP (bits 37-50) = 0x2980 (10624); CPSM (bits 51-54) = 2 (PSMCT16 CLUT); CSM (bit 55) = 0 (CSM1, 16 entries); CSA (bits 56-60) = 0 (CLUT entry offset); CLD (bits 61-63) = 1 (load the CLUT; inferred from the GS manual) | Default texture 0 of context 1: 128x128 4-bit texture at block 0x2580, CLUT at block 0x2980. |
| ALPHA_1 (0x42) | 0x44 | A (bits 0-1) = 0 (Cs); B (bits 2-3) = 1 (Cd); C (bits 4-5) = 0 (As); D (bits 6-7) = 1 (Cd); FIX (bits 32-39) = 0 | Cv = ((Cs - Cd) x As) / 128 + Cd (the /128 is from the GS manual; inferred). Context 1. |

Not written by the chain: TEST_1/2, ZBUF_1/2, FRAME_1/2, ALPHA_2, SCISSOR_2, TEX0_2, TEX1_2, XYOFFSET_1/2, DTHE, FBA. (COLCLAMP, PRMODECONT, TEXA and FOGCOL are written, and they are context-independent.)

---

## 6. Output

No VU output. The chain produces GS register state only. No GIF image data, no XGKICK, no buffer.

---

## 7. Culling and clipping

None. Scissor is set (SCISSOR_1 above) but nothing in this chain clips or culls.

---

## 8. Native replacement notes

### 8.1 Initial DrawState

A native replacement of mgInit's effect must leave the following state after init, before any render packet:

| Register | Value | Source |
|---|---|---|
| COLCLAMP | CLAMP = 1 | chain |
| SCISSOR_1 | x 0..639, y 0..223 (inclusive) | chain |
| PRMODECONT | AC = 1 | chain |
| TEXA | TA0 = 128, AEM = 1, TA1 = 128 | chain |
| TEX1_1 | 0x61 | chain |
| FOGCOL | 200, 200, 200 | chain |
| TEX0_1 | 0x20153005DD412580 (section 5) | chain |
| ALPHA_1 | 0x44 | chain |

These are what the GS holds after this chain, subject to section 9, item 1. Every render packet later rewrites the registers it needs (section 8.2), so the chain matters mainly for the registers no render packet writes.

### 8.2 What later code overwrites

- Render packets (`mg_visual.cpp:1386-1416`, `mg_sprite.cpp:188-212`, `water.cpp:544-565`, `mg_shadow.cpp:352-364`) write PRMODECONT (0), PRMODE, FOGCOL and the context-1 TEST, ZBUF and ALPHA through the draw env (`mgCDrawEnv::Initialize`, `mg_drawenv.cpp:21-39`). They do not write COLCLAMP, SCISSOR_1, TEXA, TEX1_1 or TEX0_1.
- Texture and primitive code writes TEX1_1 and TEX0_1 (`mg_drawprim.cpp:391-393`, `mglib.cpp:1579-1580`, `mglib.cpp:1620`) and TEXA (`mg_tanime.cpp:197`, `effectlist.cpp:446-512`, `gyorace.cpp:1315`). After init, TEX0_1, TEX1_1 and TEXA keep the chain values until one of those paths runs.
- SCISSOR_1 is set by the menu and message-window paths (`menudraw.cpp:1381-1389`, `nd_meswin.cpp:3905`, `prespr.cpp:45`). Until one of those runs, the chain value stays.

### 8.3 mgInit's C-side caches (never sent)

`mgInit` assigns C-side GS structs that are never sent to the GS during init (grep: no packet is built from them in `mglib.cpp:484-492`):

| Cache | Value set | Chain value | Comment |
|---|---|---|---|
| mgTEX1_1 / mgTEX1_2 | 0x261 (`mglib.cpp:484-485`) | 0x61 | Differs by MTBA (bit 9). Not sent: the GS keeps 0x61. |
| mgTEST_1 / mgTEST_2 | 0x5000B (`mglib.cpp:486-487`) | not in chain | ATE=1, ATST=GEQUAL, ZTE=1, ZTST=GEQUAL. Sent only by `mglib.cpp:1562, 1605` on their own paths. Every render draw env sets TEST (`mg_drawenv.cpp:27`). |
| mgALPHA_1 / mgALPHA_2 | 0x44 (`mglib.cpp:489-490`) | 0x44 | Same value. |
| mgTEXA_1 / mgTEXA_2 | 0x100400000 (`mglib.cpp:491-492`) | 0x0000008000008080 | Differs: TA1 = 1, TA0 = 0, AEM = 0, plus bit 22 set (in the `pad16` field of `sceGsTexa`, `libgraph.h:245-251`). Not sent by init. |

Native replacement: use the chain values for the GS, not the caches. If mgTEXA_1 is ever sent, its bit 22 must be decided first (no reader found).

### 8.4 Context and VIF

- Context 2 is not initialised by the chain. Draw env packets set TEST_2, ZBUF_2 and ALPHA_2 (`mgCDrawEnv::Initialize(1)`, `mg_drawenv.cpp:35-37`) when they are used.
- VIF state (section 2) has no effect on the DIRECT data. A native replacement may omit it.

---

## 9. Open points

1. **SDK writes after the chain.** `sceGsSetDefDBuff` (`mglib.cpp:465`) and `sceGsSwapDBuff` (`mglib.cpp:521`) come before and after the chain. They are SDK functions not in the repo (`ps2/include/sce/libgraph.h:607-608` declares them). They may write SCISSOR_1, PRMODECONT, COLCLAMP, ALPHA_1, TEST_1, ZBUF_1 and FRAME_1. The after-init value of those registers is therefore unconfirmed. Compare SCISSOR_1 with the screen size: x max 639 and y max 223 must equal `mgScreenWidth - 1` and `mgScreenHeight - 1` on the target (the menu code uses that form, `menudraw.cpp:1389`). The screen height constant was not checked.
2. **Context 2.** The chain writes no context-2 register. Whether any path draws in context 2 before a draw env packet sets its registers was not checked.
3. **TEX0_1 placement.** TBP0 = 0x2580 (block 9600) and CBP = 0x2980 (block 10624) are default texture and CLUT locations. Whether these overlap the frame or Z buffers set by `sceGsSetDefDBuff` (`mglib.cpp:465`) was not checked against the mgDBuff addresses.
4. **Meaning of CLD = 1, AEM = 1 and the /128 alpha scale.** Taken from the GS manual; not checked against the hardware or an emulator.
5. **mgTEXA_1 bit 22.** The cache value has a stray bit in the padding. No reader was found (section 8.3), so this is dormant, not a bug in use.

---

## 10. Test vectors

Not applicable. There is no VU code. Section 5 is the reference for the register values.
