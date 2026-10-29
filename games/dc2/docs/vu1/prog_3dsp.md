# Vu_prog_3dsp (VU1)

Camera-facing billboard program for `mgC3DSprite` (`ps2/src/mg_sprite.cpp:84-432`). A render-info packet first runs `Vu_progmain` (MSCAL 0) to load the local-to-screen matrix, the view-scale rows, fog and clip constants. Each batch of up to 33 sprites is then DMAed to TOPS (VU data 60 or 180) and run from micro 0x64 (MSCAL 2 or MSCNT). Per sprite the program transforms the centre by the local-to-screen matrix, builds four corners (PRIM sprite, mode 0: two used) or a view-rotated quad (PRIM strip, mode 1: four used), divides by w, culls the sprite as a whole against 12.4 screen bounds [1, 4095] in x/y and against near < Pw < far, computes a fog byte from 1/w, and writes a GIF packet of `1 + 5N` (mode 0) or `1 + 9N` (mode 1) quadwords at VU 420 or 600, which it XGKICKs. There is no clipping and no scissor test; the only rejection is the all-or-nothing ADC path described in section 7.

Addresses are micro pair addresses (MSCAL/branch) and VU data quadword addresses (lq/sq/ilw/iswr). Listing references are to `port/build/vu1/prog_3dsp.txt` (`P:` below) and `port/build/vu1/progmain.txt` (`M:`). CPU references are `mg_sprite.cpp` unless stated.

---

## 1. Role and entry points

- **Upload.** `mgC3DSprite::Draw` (`mg_sprite.cpp:226-257`) issues the render-info CALL (`:240-245`), then `mgSendVuProg(cursor, 2)` (`:248`), then the batch CALL (`:249-251`). The upload is an MPG of 214 instructions at micro 0x64 (`cpu_feed_other.md` 0.3, not re-decoded here). Micro 0x00-0x54 (Vu_progmain) is never overwritten by it.
- **Render-info packet (MSCAL 0).** `CreateRenderInfoPacket` (`:84-224`) ends with `program_call = MSCAL 0` (`:145`). Entry 0 is Vu_progmain at pair 0x0000 (`M:0000`): `bal vi15, __main`. `__main` (`M:0006-0053`) loads constants and returns with `jr vi15` (`M:0053`); its delay-slot pair `M:0054` carries E, so the program halts there. The halt PC is the `jr` target, 0x0002 (inferred). This packet does not start the billboard program. Its halt PC (0x0002) is irrelevant: each batch packet begins with its own `MSCAL 2` (below), so no MSCNT follows this MSCAL 0 (checked).
- **Batch (MSCAL 2 / MSCNT).** The first batch of a packet issues `MSCAL 2` (`prog_vif`, `mg_sprite.cpp:378`, emitted at `:402-404`; `prog_started` is reset per packet at `:269`). Pair 0x0002 of Vu_progmain is `iaddiu vi01, vi00, 100; jr vi01` (`M:0002-0004`), which jumps to 0x0064 = `__INIT_DATA`. Later batches issue `MSCNT` (`:381`, `:402-407`), which resumes at the halt PC left by the previous batch.
- **Halt PC of a batch.** The batch ends with an E pair whose branch targets `__INIT_DATA`: `P:0074` (rotated path, `nop[E] | b __INIT_DATA`) or `P:0078` (non-rotated path). Both halt with PC = 0x0064, so MSCNT resumes at `__INIT_DATA`.
- **Flush.** Every batch is followed by `VIF FLUSHA` (`mg_sprite.cpp:409-410`), so a batch's VU run and its GIF transfer finish before the next batch's DMA data arrives (inferred from FLUSHA semantics).
- **What persists** across calls: all vi/vf/ACC/Q/I registers (section 2), and all VU data memory (section 3). Upload does not reset registers.

## 2. Register conventions

| Reg | Set by (pair) | Meaning |
|---|---|---|
| vi00 | hardware | 0 |
| vf00 | hardware | (0,0,0,1) |
| vi12 | `xtop` P:0064 | TOPS: input base of this batch (60 or 180, section 3) |
| vi14 | `iaddiu` P:0065 (1024) | Stack pointer; one return address at data 1023 (P:007A-007B, P:00CB-00CC; rotated: P:00D1-00D2, P:0134-0135) |
| vi15 | `bal` (P:0076 / P:0072) | Link: pair after the delay slot (0x0078 / 0x0074) |
| vi13 | Progmain `M:001F` = 420 on each render-info packet; then toggled P:0068-006C | Output base of this batch: 420 (0x1A4) or 600 (0x258) |
| vi01 | `ilw.y 1(vi12)` P:006E (mode from header) | Mode word; `ibeq vi01, vi00` P:0070 selects non-rotated (0) vs rotated (any nonzero) |
| vi11 | `ilw.x 1(vi12)` P:006D | Sprite count N (header x) |
| vi09 | `iaddi` P:0086 (= N) | Loop counter; decremented P:00B0 (non-rot) and P:012D (rot) |
| vi02 | `iaddiu` P:006F (= TOPS+2) | Record base |
| vi08 | `iaddi` P:0087 (= vi02) | Current record pointer; `+5` P:00AD (non-rot) or P:012E (rot) |
| vi06 | `iaddi` P:0080 (= vi13) | Output pointer; `+5` P:00C8 or `+9` P:012F |
| vi07 | P:0081-0083, P:00B4-00B5 (flags), P:011E-011F | Scratch, then the sign-flag mask |
| vi10 | `iaddiu` P:00B3 / P:011B (= 208) | Flag mask 0xD0 |
| I | `loi 0.5` (P:00A3, P:00C2, P:00FE, P:00EF); `loi 2048.0` (P:00BB, P:0120) | Immediate for the `*I` ops in the next pair |
| Q | `div` P:008A, P:008B, P:009F, P:00FF, P:00C6-C8 (rot: P:00E1, P:00E2) | Reciprocal (see section 5) |
| ACC | `mulax`/`madd` chains | Accumulator for the LS transform and fog |
| vf10-vf13 | Progmain P:0015, 0016, 0019, 001A (from data 4-7) | Rows of the local-to-screen matrix LS. **Inherited** (section 11) |
| vf15 | `lq 0(vi12)` P:007F | GIF tag from the batch (copied into out[0].yzw) |
| vf16, vf17 | `lq 25`, `lq 26` P:008E-008F (non-rot) | VS rows 0 and 1 (view_screen, scaled by matrix row norms) |
| vf16, vf17 (rot) | P:00EF-00F2, scaled P:00F8-00FB | Rotated axes A and B (section 5) |
| vf18, vf19 (rot) | `lq 25`, `lq 26` P:00F4-00F5 | VS rows 0 and 1, rotated path only |
| vf19 | `sub` P:007F (= 0); `mulq.xy` P:00A8 / P:00C8 | Scaled half extent (xy), zw = 0 |
| vf20 | `lq 0(vi08)` (P:0093, P:00AE, P:00EB) | Sprite position |
| vf21 | `lq 1(vi08)` | Sprite size (w, h, sin, cos) |
| vf22 | `lq 2(vi08)`, `ftoi0` | Colour. Non-rot: P:00B0 writes s22 (sign-test difference) into it, then the `lq` at P:00B1 overwrites it with colour before `ftoi0` (P:00B6); the cull reads only the flags. Rot: min corner (P:0117), then s22 (P:011A) |
| vf23 | `lq 3(vi08)`, `ftoi4` | UV0. Non-rot: P:00B1 writes s23 into it, then the `lq` at P:00B2 overwrites it with UV0 before `ftoi4` (P:00B9). Rot: max corner (P:0114), then s23 (P:011B) |
| vf24 | `lq 4(vi08)`, `ftoi4` | UV1 |
| vf25 | Transform P:0098-009B (rot P:00F4-00F7) | Centre: (Px/Pw, Py/Pw, Pz/Pw, 1/Pw) after divide |
| vf26.w | `maddw.w` P:00AF | Fog value (clamped) |
| vf27, vf28 | `sub`/`add` P:00AC-00AD (rot P:010C-010D, P:010E-010F) | Corner min and max (non-rot); rot corners (section 5) |
| vf29 | `lq 59(vi00)` P:008C | Fog and clamp constants: (offset, near_value, far_value, scale), section 3 |
| vf30 | `lq.xyzw 22(vi00)` P:0089 (x, y, w used); `addq.w` P:0091 (w) (rot P:00E8) | (1, 1, *, 1/far) |
| vf31 | `lq.xyzw 21(vi00)` P:0088 (x, y, w used); `addq.w` P:0092 (w) (rot P:00E9) | (4095, 4095, *, 1/near) |

Constants that the program relies on: vf30.xy = (1,1) and vf31.xy = (4095,4095), loaded from data 22/21 and set by Progmain (section 11). Their w values are not constants: this program sets vf30.w = 1/far and vf31.w = 1/near (section 5).

## 3. Data memory map

Quadword addresses. "CPU" = written by `mgC3DSprite::CreateRenderInfoPacket` (`mg_sprite.cpp`) or `mgFlushRenderInfo` (`mglib.cpp:1228-1250`). "VU" = written by Vu_progmain or this program.

| Addr (qw) | Contents | Writer | Readers |
|---|---|---|---|
| 0x00-0x02 | zeros (`unk_20`) | CPU `:111-113` | none |
| 0x03 | render_params: x raw, y,z,w floats | CPU `:114-117` | none in this program |
| 0x04-0x07 | LS = local_screen (`world_screen` x `matrix`), column k in data 4+k | CPU `:100, :118` | Progmain `M:0015-001A` to vf10-13; this program via vf10-13 |
| 0x08-0x0B | local_world (object matrix) | CPU `:119` | Progmain only (vf15-18, consumed inside progmain) |
| 0x0C-0x16 | unk_e0 (not written by any CPU code in this packet) | none | Progmain reads 0x0C-0x13 (`M:000A-001C`), results unused by this program. Contents are stale data, not defined here |
| 0x17 | fog copy {offset, near_value, far_value, scale} | CPU `:123-126` | **none**: this program and Progmain do not read it (section 9, item 9) |
| 0x18 | tail unk_00 (not written) | none | Progmain `M:0017` loads vf25 from it; overwritten, unused |
| 0x19 | VS row 0 (view_screen[0], `.x` scaled by `\|matrix row 0\|`) | CPU `:131-136` | P:008E (vf16); P:00F4 (vf18), rot |
| 0x1A | VS row 1 (`.y` scaled by `\|matrix row 1\|`) | CPU `:132-137` | P:008F (vf17); P:00F5 (vf19), rot |
| 0x1B | VS row 2 (scaled by `\|matrix row 2\|`) | CPU `:133-138` | none |
| 0x1C | VS row 3 (unscaled) | CPU `:134` | none |
| 0x1D-0x25 | unused | none | none |
| 0x26 | draw flags (bit 0 CLIP, bit 1 SCISSOR, bit 2 PROGRAM_OPTION, bit 3 PROGRAM_MODE, bit 4 POINT_LIGHT, bit 5 NO_LIGHT) | CPU `:148-183` | Progmain `M:0044`. SCISSOR is always 0 here (`render_info->scissor = 0`, `:120`) |
| 0x15 (21) | (x, y, z, w): xy = (4095, 4095) when SCISSOR clear (else guard_max.xy); w = 0x39.w = far (always). Stored by Progmain `M:004E` from vf15 | VU (Progmain) | P:0088 (lq into vf31); P:00DF (rot) |
| 0x16 (22) | (x, y, z, w): xy = (1, 1) when SCISSOR clear (else guard_min.xy); w = 0x3A.w = near (always). Stored by Progmain `M:004F` from vf16 | VU (Progmain) | P:0089 (lq into vf30); P:00E0 (rot) |
| 0x1E (30) | 0 or 32 (Progmain `M:0042`) | VU (Progmain) | none |
| 0x2C (44) | Progmain `M:0020` | VU (Progmain) | none |
| 0x39 (57) | guard_max: (guard_max.x, .y, guard_max.z = 0, guard_max.w = far) (`mg_drawenv.cpp:145-146`) | `mgFlushRenderInfo` `mglib.cpp:1246` | Progmain `M:0046` (lq into vf15), then `M:004E` stores vf15 to data 21 |
| 0x3A (58) | guard_min: (guard_min.x, .y, guard_min.z = 0, guard_min.w = near) (`mg_drawenv.cpp:141-142`) | `mgFlushRenderInfo` `mglib.cpp:1247` | Progmain `M:0047` (lq into vf16), then `M:004F` stores vf16 to data 22 |
| 0x3B (59) | fog: (offset, near_value, far_value, scale) | `mgFlushRenderInfo` `mglib.cpp:1236-1241` (words 4-7) | P:008C -> vf29; P:00E3 (rot) |
| 0x3C (60) | BASE: batch input buffer A (TOPS value) | CPU `:109` (VIF BASE) and batch UNPACK (FLG=1) | this program via vi12 |
| 0xB4 (180) | OFFSET: batch input buffer B (TOPS value) | CPU `:110` (VIF OFFSET) and batch UNPACK | this program via vi12 |
| TOPS+0 | GIF tag template (qw) | CPU `:317-346` via UNPACK at TOPS | P:007F (vf15 yzw copied into out[0]) |
| TOPS+1 | header {x = N, y = mode, z = 0, w = 6} | CPU `:389-392` | P:0067 (`ilw.y`), P:006D (`ilw.x`), P:006E (`ilw.y`) |
| TOPS+2+5i | sprite i: pos, size, colour, uv0, uv1 (section 5) | CPU `:350-368` | P:0093-0097 (non-rot), P:00EA-00EE (rot) |
| 420 (0x1A4) | output buffer A: GIF tag + 5N or 9N qw | VU | XGKICK at P:00C9 / P:0132 |
| 600 (0x258) | output buffer B | VU | XGKICK |
| 1023 (0x3FF) | return address (`iswr.x vi15`) | VU | `ilwr.x vi15` (P:00CB / P:0134) |

Input and output extents: input is `TOPS + 2 + 5N` quadwords with N <= 33, so at most 167 quadwords from TOPS (60..226 or 180..346), below 420. Output (mode 0) is `1 + 5N <= 166`, from 420 to 585 or from 600 to 765. Output (mode 1) is `1 + 9N <= 298`, from 420 to 717 (this overlaps buffer B at 600; see section 9, item 11). Stack at 1023 is below all of these.

## 4. Control flow

Batch entry (`P:0064-0076`), pseudo-code:

```
TOPS  = xtop                          // P:0064, vi12
vi14  = 1024                          // P:0065
vi01  = header.y ; vi11 = header.x    // P:0067, P:006D, P:006E
vi13  = (vi13 == 420) ? 600 : 420     // P:0068 ibeq, P:006B / P:006C
if vi01 == 0 : bal non_rot            // P:0070 -> P:0076 (bal __MAIN_PROG)
else         : bal rot                 // P:0072 -> P:0072 bal __MAIN_PROG_ROT
halt (E pair P:0074 or P:0078) ; next run starts at 0x0064
```

Subroutine frame (both paths): `vi14 -= 1; iswr.x vi15 -> data[vi14]` at entry; `ilwr.x vi15 <- data[vi14]; vi14 += 1; jr vi15` at exit (`P:007A-007B` / `P:00CB-00CF`; rotated `P:00D1-00D2` / `P:0134-0138`). One level of call.

Non-rotated path (`P:007A-00CF`):

```
prologue: out[0] = GIF tag (section 6); vi06 = out+1
                    constants (section 5, "per batch")
loop (__LPmain, P:00AA-00C7), N iterations, one sprite each:
    emit sprite i (colour, uv0, uv1, corners)       // stores P:00AA-00C6
    compute sprite i+1 transform (pipelined)        // P:00AE-00C8
    vi09 -= 1 (P:00B0); repeat while vi09 != 0 (P:00C7)
xgkick vi13 (P:00C9)
```

Rotated path (`P:00D1-0139`): same prologue, then a non-pipelined loop `__LPmain_rt` (`P:00EA-0130`) computing each sprite in full; repeats while `vi09 != 0` (`P:0130`).

Branches on flag words:

| Pair | Test | Meaning |
|---|---|---|
| P:0070 `ibeq vi01, vi00` | mode == 0 | Non-rotated (0) vs rotated (nonzero) |
| M:004A `ibne` (Progmain) | flags.bit1 | SCISSOR; always 0 for this CPU, so Progmain takes the full-screen constants (section 11) |
| P:00BD `ibeq vi07, vi10` | vi07 == 0xD0 | Sprite passes the cull (section 7). If equal, skip the two `+2048` (ADC) adds (P:00BF, P:00C0) |
| P:0121 `ibeq vi07, vi10` | same | Rotated: skip `+2048` on v2 and v3 only (P:0123, P:0124). v0 (P:0121) and v1 (P:0122, delay slot) always get `+2048` |

Loop counter: vi09 = N (`P:0086`), decremented once per sprite. N >= 1 is required: the CPU emits only batches with `sprite_num > 0` (`mg_sprite.cpp:394`). With N = 0 the loop would run about 65535 times (vi09 wraps to -1). Not guarded by the program.

## 5. Per-element math

Notation: `p` = vf20 (pos), `s` = vf21, `c` = colour (vf22), `u0` = vf23, `u1` = vf24 (all as loaded). `LS[k]` = VU data 4+k (vf10+k, k = 0..3). `VS0` = VU data 25 (vf16 before scaling), `VS1` = VU data 26. `Pw` etc. are per sprite. The program treats every `*` below as exact, with FMAC truncation toward zero (section 8).

### Per batch (P:007F-0092; rotated copy P:00D6-00E9)

VF operands are read in program order (interlocked). Q is not interlocked: a DIV result is visible 7 pairs after its DIV (section 9, item 7).

```
G        = TOPS[0]                         // GIF tag template, vf15 (P:007F)
out[0].x = 0x00008000 | N                 // P:0083-0084 iswr (zero-extended); EOP = bit 15
out[0].yzw = G.yzw                         // P:0085 sqi.yzw (PRE, PRIM, NREG, regs copied)
vf16 = VS0 ; vf17 = VS1                    // P:008E-008F (non-rot)
vf29 = data[0x3B] = (off, near_v, far_v, scale)     // P:008C
vf31 = data[0x15] = (4095, 4095, *, far)           // P:0088 lq; xy as set by Progmain (SCISSOR clear)
vf30 = data[0x16] = (1, 1, *, near)                // P:0089 lq; xy as set by Progmain (SCISSOR clear)
Q      = 1 / vf31.w = 1/far                        // P:008A div: reads vf31.w = far (loaded at P:0088)
vf30.w = vf19.w + Q = 1/far                        // P:0091 addq.w; vf19.w = 0 (P:007F); Q of P:008A visible here
Q      = 1 / vf30.w = 1/near                       // P:008B div: reads the loaded vf30.w = near (P:0089)
vf31.w = vf19.w + Q = 1/near                       // P:0092 addq.w; Q of P:008B visible here
```

Result: `vf30 = (1, 1, *, 1/far)` and `vf31 = (4095, 4095, *, 1/near)`. The swap comes from each DIV reading the other register's w: P:008A reads vf31.w and lands in vf30.w; P:008B reads vf30.w and lands in vf31.w. P:008B reads the new (loaded) vf30.w, not a stale one (ruling a; the stale variants are item 15 only).

Rotated copy (P:00DF-00E9): `lq` vf31 (P:00DF), `lq` vf30 (P:00E0), DIV P:00E1 (reads vf31.w), DIV P:00E2 (reads vf30.w), `addq.w vf30` P:00E8, `addq.w vf31` P:00E9. Same distances (7 pairs), same values.

Window used by the cull (section 7): `1/far < 1/Pw < 1/near`, i.e. `near < Pw < far`, with `near = data[0x16].w = 0x3A.w` and `far = data[0x15].w = 0x39.w`. Both w values are the guard w values in both SCISSOR cases (section 11).

### Per sprite, non-rotated (P:0098-00C8)

```
// transform: centre in clip space
Pclip = sum_k p[k] * LS[k]                 // mulax/madday/maddaz/maddw, P:0098-009B
Pw    = Pclip.w                            // ACC chain; Pw = view depth (screen [2][3] = 1)
Q     = 1 / Pw                              // div, P:009F (7 pairs)
c     = (Pclip.x, Pclip.y, Pclip.z, 1) * Q  // mulq.xyz P:00A6, mulq.w P:00A7 => vf25 = (Px/Pw, Py/Pw, Pz/Pw, 1/Pw)

// half extents in clip units (before /Pw)
half.x = s.x * VS0.x * 0.5                 // mulx P:009F (vf21.x = s.x*vf16.x), muli.xy P:00A4 (*0.5)
half.y = s.y * VS1.y * 0.5                 // muly P:00A0 (vf21.y = s.y*vf17.y)
hq     = (half.x, half.y) * Q              // mulq.xy P:00A8 -> vf19.xy ; vf19.zw = 0

// corners
min    = c - (hq.x, hq.y, 0, 0)            // sub.xyzw P:00AC -> vf27
max    = c + (hq.x, hq.y, 0, 0)            // add.xyzw P:00AD -> vf28
// after P:00BB/P:00BF/P:00C0 and P:00C1/P:00C2 (ftoi4), vf27.w and vf28.w = fog F (+2048 if culled)
```

So `vf27 = (cx - hq.x, cy - hq.y, cz, 1/Pw)` and `vf28 = (cx + hq.x, cy + hq.y, cz, 1/Pw)`, with `cz = Pz/Pw`. Corner x = `(Px -/+ half.x) / Pw`, i.e. the half extent is applied before the divide.

Fog and sign flags:

```
F0    = offset + scale * (1/Pw)            // maddw.w P:00AF: ACC.w = vf29.x (offset), vf26.w = ACC.w + vf29.w * vf25.w
F1    = min(F0, near_value)                // miniy.w P:00B3, vf29.y = near_value
F     = max(F1, far_value)                 // maxz.w P:00B7, vf29.z = far_value
```

The fog value is the clamp `F = max(min(F0, near_value), far_value)`. The order of the two clamps matters if far_value > near_value (then F = far_value).

Cull flags (see section 7 for the decision):

```
s22 = vf30 - min          (x, y, w)        // sub.xyw P:00B0 -> vf22
s23 = max - vf31          (x, y, w)        // sub.xyw P:00B1 -> vf23
vi07 = signbits(s22.x, s22.y, s22.w, s23.x, s23.y, s23.w) & 0xD0   // fmand P:00B4, P:00B5
```

### Per sprite, rotated (P:00EA-0130; axes at P:00EF-00FB)

```
// axes (rotation of the quad about the view direction, angle from the CPU's sin/cos)
cos = s.w ; sin = s.z                       // CPU slot[2] = sinf(angle), slot[3] = cosf(angle)
A   = (cos, sin)  * VS0.x * (s.x * 0.5)        // P:00EF-00F0 (vf16 = (cos, sin)), P:00F8, P:00FA
B   = (-sin, cos) * VS1.y * (s.y * 0.5)        // P:00F1-00F2 (vf17 = (-sin, cos)), P:00F9, P:00FB
// VS0 and VS1 already carry the matrix row norms s0, s1 (CPU scales view_screen rows, mg_sprite.cpp:136-137)

// centre, same as non-rotated: vf25 = (Px, Py, Pz, 1) * LS ... / Pw, Q = 1/Pw (P:00F4-00FF)
A' = A * Q ; B' = B * Q                     // mulq P:0106 (vf22 = A'), P:0109 (vf23 = B')

// four corners (P:010A-010F)
vf15 = A' + B'                              // P:010A add.xy
vf16 = B' - A'                              // P:010B sub.xy
v0 = c - vf15   -> vf27                     // P:010C   [strip vertex 0, out qw 2]
v1 = c - vf16   -> vf17                     // P:010E   [strip vertex 1, out qw 4]  = c + A' - B'
v2 = c + vf16   -> vf18                     // P:010F   [strip vertex 2, out qw 6]
v3 = c + vf15   -> vf28                     // P:010D   [strip vertex 3, out qw 8]
```

With `A' = (ax, ay)` and `B' = (bx, by)`: `v0 = c - (A'+B')`, `v1 = c + A' - B'`, `v2 = c - A' + B'`, `v3 = c + A' + B'`. This is a triangle strip order (v0, v1, v2, v3).

Colour and UV, both paths:

```
RGBAQ = ftoi0(c)                // P:009C / P:00FC   (each component truncated, saturating to 16 bits, then low 8 bits used)
UV0   = ftoi4(u0)               // P:009D / P:00FD
UV1   = ftoi4(u1)               // P:009E / P:00FE
```

No clamp or scale on colour: 0x80 is passed through as the GS value, and the caller's (128,128,128,96) reaches the GS unchanged (`cpu_feed_other.md` B.5, not re-verified against the callers).

Rotated UV layout (`P:0100-010F`), out qw index: `[1] = (u0.x, u0.y)`, `[3] = (u1.x, u0.y)`, `[5] = (u0.x, u1.y)`, `[7] = (u1.x, u1.y)`. Each UV precedes its vertex: `[1]->[2]`, `[3]->[4]`, `[5]->[6]`, `[7]->[8]`.

Final packing (both paths), `ftoi4` = `trunc(16 * x)` (saturating), see section 6:

```
X16 = ftoi4(v.x)     // 12.4; the packed X is its low 16 bits
Y16 = ftoi4(v.y)
Z   = bits 27..4 of ftoi4(v.z) = trunc(v.z)        (24-bit Z field; inferred: trunc(16 z) >> 4)
F8  = bits 11..4 of ftoi4(v.w) = trunc(v.w) & 0xFF   (fog byte, from the clamp above)
ADC = bit 15 of ftoi4(v.w) = 1 when +2048 was added
```

`v.z = Pz / Pw` (the centre z, divided), `v.w` = F (plus 2048 for ADC vertices).

## 6. Output: GIF packet, registers, XGKICK

- **GIF tag (out[0]).** `x = 0x8000 | N` (NLOOP = N in bits 0-14, EOP = bit 15) written by `iswr.x` (P:0084). The VU writes NLOOP: the CPU never sets it (`mg_sprite.cpp:319-321`, EOP only). `y`, `z`, `w` are copied from TOPS[0] (`sqi.yzw`, P:0085): the CPU's PRE = 1, PRIM = 0x5E (mode 0) or 0x5C (mode 1), NREG = 5 or 9, and the register list (`mg_sprite.cpp:325-345`). Bits 16-31 of out[0].x are written as 0: `iswr` stores the zero-extended 16-bit integer (brief ruling). The GIF tag does not use them (inferred).
- **Packing.** PACKED mode, one register per quadword per loop.
  - Mode 0 (`NREG` 5): qw0 RGBAQ (vf22), qw1 UV (vf23), qw2 XYZF2 (vf27), qw3 UV (vf24), qw4 XYZF2 (vf28). Loops: N. PRIM 0x5E (sprite: two vertices, qw2 and qw4).
  - Mode 1 (`NREG` 9): qw0 RGBAQ, then four pairs (UV, XYZF2) at qw1-8, as in section 5. PRIM 0x5C (triangle strip).
- **Packet length.** `1 + 5N` (mode 0) or `1 + 9N` (mode 1) quadwords, starting at vi13.
- **XGKICK.** `xgkick vi13` at P:00C9 (non-rot, after the loop) and at P:0132 (rot). The packet is consumed by the GS over PATH1 (section 1 of the brief). The batch's FLUSHA on the CPU side waits for this to finish.
- **Register writes in the GS state (not from the VU).** PRMODE / PRMODECONT / TEX0 come from the CPU's packet (`mg_sprite.cpp:192-215`), not from this program.

## 7. Culling and clipping

- **No clipping.** `clipw` and clip flags are not used. No vertex is clamped to the screen. The GS scissor is set by the draw environment, not by this program.
- **Sprite-level cull, mode 0 and mode 1.** A sprite is "passed" (drawn) when all six sign flags are set:
  - x: `vf30.x - min.x < 0` and `max.x - vf31.x < 0`, i.e. `min.x > 1` and `max.x < 4095`.
  - y: `min.y > 1` and `max.y < 4095`.
  - w: `vf30.w - min.w < 0` and `max.w - vf31.w < 0`, with `min.w = max.w = 1/Pw`, `vf30.w = 1/far`, `vf31.w = 1/near` (section 5). `far = 0x39.w` and `near = 0x3A.w` (`mglib.cpp:1246-1247`; set in `mg_drawenv.cpp:146` and `:142`). Window: `1/far < 1/Pw < 1/near`, equivalently `near < Pw < far`. Strict.
  - With the constants of section 3, the cull bounds are full screen (1..4095 in 12.4 units; the 2048 centre of section 5) and the depth range (near, far) from the frame setup (`mg_drawenv.cpp:136-146`: `guard_min[3] = clip_min[2]` = near_dist; `guard_max[3] = clip_max[2]` = far_dist).
  - Mode 0 uses `min`/`max` = vf27/vf28. Mode 1 uses the min and max over the four corners (P:0114-0119), computed as `vf23 = max(vf27, vf28, vf17, vf18)` and `vf22 = min(...)`.
  - Only the x, y and w sign flags are tested (mask 0xD0 in `vi10`). The z sign and zero flags of these `sub.xyw` ops are 0 (ruling c) and do not enter the test.
  - Strict inequalities: exactly-equal values fail the test.
- **Rejection.** `vi07 != 0xD0` means the test failed. Then the `+2048` ADC is added to w:
  - mode 0: both vertices (`P:00BF`, `P:00C0`, skipped on pass).
  - mode 1: v0 and v1 always (`P:0121-0122`), v2 and v3 only when the test failed (`P:0123-0124`).
  - ADC vertices are queued without kicking (GS), so a failed mode-0 sprite draws nothing; a failed mode-1 strip draws nothing; mode 1 always queues v0 and v1 (inferred GS behaviour, see section 9, item 2).
- **Whole-sprite rejection only.** A sprite that straddles a bound is rejected entirely.
- **Degenerate w.** `Pw = 0` gives `Q` = the VU's largest magnitude (inferred); the w test then fails and the sprite is rejected.

## 8. Native replacement notes

Must reproduce exactly:

- The LS transform order: `Pclip = sum_k p[k] * LS[k]` (row k of LS = column k of the matrix), with the w term included (LS[3] times p.w).
- Perspective: divide by `Pw = Pclip.w`, not by an approximated 1/w; `Q` is the divide result.
- The half extent: `half = size * VS_scale * 0.5` before the divide, and the corners are `(Px -/+ half.x)/Pw`. The half is scaled by `1/Pw` (the division is shared), not `half/Pw` after a second scale.
- The sign-based cull: strict `<` on the six differences (section 7), against 1 and 4095 in x,y and against `1/far` and `1/near` in w (window `near < Pw < far`, section 5).
- The fog: `F = max(min(offset + scale/Pw, near_value), far_value)`, then the byte is `trunc(F) & 0xFF`.
- The GIF tag: NLOOP = N, EOP = 1, rest copied from the batch's tag. Output register order exactly as section 6.
- The strip vertex order for mode 1: v0 = c - (A+B), v1 = c + A - B, v2 = c - A + B, v3 = c + A + B.
- The ADC pattern: mode 0 both or neither; mode 1 v0 and v1 always, v2 and v3 only on failure (inferred intent; see section 9, items 1-2).

May approximate:

- VU floats are truncated, with no NaN, Inf or denormals. A native reimplementation can use IEEE single precision with truncation or round-to-nearest. A difference is at most one unit of the 12.4 coordinate (1/16 px) and one colour or fog count. Truncation of colours and UV is already part of `ftoi0/ftoi4`, so keep it.
- `sin` and `cos` are the CPU's `sinf`/`cosf` (`mg_sprite.cpp:355-360`), reproduced as-is.

CPU-side constants the native path needs (inherited from the frame, not from the sprite packet): LS (local_screen), VS rows scaled by the matrix row norms, fog {offset, near_value, far_value, scale} from `mgFlushRenderInfo` (`mglib.cpp:1236-1241`), and {near, far} from `guard_min[3]` / `guard_max[3]` (`mg_drawenv.cpp:142, :146`).

## 9. Open points

1. **VF hazards (ruled: interlocked).** VF reads follow program order, so the rotated corners (P:0109 -> P:010A/P:010B -> P:010C-P:010F), the cull (P:00B0/P:00B1 -> P:00B4/P:00B5; P:0114-P:011B) and the ADC (P:00BF/P:00C0 -> P:00C1/P:00C2; P:0120-P:0124 -> P:0125-P:0128) are computed exactly as written. No stale-value reading is used. ACC chains follow program order (ruling b): P:00AE -> P:00AF and P:0110 -> P:0111 read the ACC value of the previous pair. MAC flags are not VF data: `fmand` reads the flags of the FMAC op 4 pairs earlier (P:00B0/P:00B1 at P:00B4/P:00B5; P:011A/P:011B at P:011E/P:011F), which the nop padding matches.
2. **Non-rot ADC.** P:00BF/P:00C0 `addi.w` (+2048) are read by P:00C1/P:00C2 `ftoi4` in program order, so failed mode-0 sprites are queued with ADC set and are not drawn. The GS behaviour for an ADC-queued vertex is assumed, not verified.
3. **Mode values other than 0 and 1.** `CreateRenderInfoPacket` writes the raw mode into the header (`mg_sprite.cpp:390`), and `BeginCPSprite` treats any mode other than 1 as the sprite (`:337-346`). The VU branches to the rotated path for any nonzero mode (P:0070). For mode 2 or higher the GIF is a 5-register sprite while the VU emits 9-register strips. `mg_sprite.hpp:28` documents "any value other than ROTATE acts as UPRIGHT". The callers' values were not checked; no caller is known to pass a value other than 0 or 1.
4. **TOPS sequence.** `xtop` returns TOPS = 60 or 180 (`mg_sprite.cpp:109-110`). Which buffer the first batch of a packet uses, and whether `xtop` returns the buffer the VIF just filled (inferred from the brief's flip rule), was not determined. The program does not toggle TOPS; it only reads it.
5. **Flags word race.** Flags are sent by an UNPACK after the MSCAL 0 with no FLUSH (`mg_sprite.cpp:145` then `:176-179`). Progmain reads 0x26 at `M:0044`. The result depends on whether the VIF has written 0x26 by then. Harmless today: SCISSOR is always 0 (`:120`). It would matter for uninitialised memory at startup.
6. **vi13 on the first batch.** Vi13 = 420 is set by Progmain at `M:001F`, and the first batch toggles it to 600. This relies on MSCAL 0 running before the batch in the same packet, which holds by construction (`:145`, `:378`).
7. **DIV timing (P:008A/P:008B; rotated P:00E1/P:00E2).** Q is not interlocked. The window needs P:0091 to see the P:008A result and not the P:008B result, and P:0092 to see the P:008B result. That holds only for a Q latency of exactly 7 pairs (inferred from P:009F -> P:00A6, the program's own use): a latency of 6 or less makes P:0091 read the P:008B result (1/near, empty window), 8 or more makes P:0091 read the Q left by the previous batch (P:00BC or P:00FF). Whether two outstanding DIVs complete in issue order is inferred. A stall of the second DIV moves the time but not the Q sequence, so the w values do not change. Confirm with an emulator trace.
8. **Fog and guard constants come from the frame, not the batch.** `mgFlushRenderInfo` (`mglib.cpp:1228`) writes data 0x39-0x3B and is called once per frame from `mgBeginFrame` (`mglib.cpp:685`, function starting `:637`). Every sprite packet in the frame uses the same values. The enclosing function's timing was not traced further.
9. **Fog at 0x17 is dead for this program.** `cpu_feed_other.md` B.3 says the VU reads fog at 0x17. It does not: Vu_prog_3dsp reads 0x3B (`P:008C`), and no listing reads 0x17. `mg_sprite.cpp:123-126` writes it for nothing; either it is a leftover from another program or a bug.
10. **Cull constants are not written by the sprite packet.** Data 21/22 come from Progmain; the values used are full-screen (1, 4095) only because SCISSOR is 0. A future CPU change to `scissor` would switch the cull to the guard band (`guard_min`/`guard_max`), which this program supports (`M:004A`).
11. **Rotated output overlap.** A mode-1 batch with N = 33 writes 298 quadwords from 420 (to 718), which overlaps buffer B (600). Safe only because each batch is FLUSHA-serialised (`mg_sprite.cpp:409`) (inferred). Confirm that the GS has consumed the packet before the next batch writes.
12. **N = 0.** Not guarded (section 4). The CPU never sends it.
13. **Callers' data.** The meaning of `first`, `second`, `third`, `fourth`, `fifth` (position, size and angle, colour, UV corners) is taken from `cpu_feed_other.md` B.5 (`dng_effect.cpp:837-863`, `editeff.cpp:385, :483`); the callers were not re-read.
14. **Degenerate Pw.** A sprite at the eye produces `Pw = 0` (inferred Q behaviour). Not analysed further.
15. **Stale-read variants of the w window (open; the behaviour is the interlocked result of section 5, ruling a).** The interlocked result is the window `near < Pw < far`. The variants below are kept only as alternatives to rule out with an emulator trace at P:0088-P:0092 and P:00DF-P:00E9. Neither gives the `near..far` window:
    - S1: P:008B reads the previous vf30.w (1/far, left by the previous batch's P:0091; undefined on the first batch). Q = far, so vf31.w = far. Window `1/far < 1/Pw < far`, i.e. `1/far < Pw < far`. near is not used.
    - S2: P:008A reads the previous vf31.w (1/near, left by the previous batch's P:0092). Q = near, so vf30.w = near. P:008B reads the loaded vf30.w (near), so vf31.w = 1/near. Window `near < Pw < 1/near`. This is the only variant that uses near. With the section 10 values (near = 1) the window is empty and every sprite is rejected.
16. **Integer (VI) results on the cull branches (settled by ruling b).** The `fmand vi07` chains (P:00B4 -> P:00B5; P:011E -> P:011F) and the `ibeq vi07` at P:0121 read program-order values, so a vi result is visible to the next pair. No alternative reading is kept.

## 10. Test vectors

Common inputs (VU data, as the CPU would write them):

- LS (data 4-7, vf10-13): col0 = (2,0,0,0), col1 = (0,2,0,0), col2 = (0,0,1,0), col3 = (8192, 8192, 40, 4).
- VS0 = (2,0,0,0) in data 25 (vf16), VS1 = (0,2,0,0) in data 26 (vf17). Scales s0 = s1 = 1 (identity matrix).
- Fog (data 0x3B): offset 0.5, near_value 1.0, far_value 0.0, scale 8.0. Bounds: data 21 w = 0x39.w = far = 200; data 22 w = 0x3A.w = near = 1. Interlocked: P:008A Q = 1/200 (from vf31.w = 200), visible at P:0091, so vf30.w = 1/200. P:008B reads the loaded vf30.w = 1, so Q = 1 and vf31.w = 1 at P:0092. So near = 1, far = 200.
- Colour (128,128,128,96). UV0 = (0,0). UV1 = (16,16).

Derived constants: vf30 = (1, 1, 0, 1/200), vf31 = (4095, 4095, 0, 1/1). Window: 1/200 < 1/Pw < 1, i.e. 1 < Pw < 200. The three cases below have Pw = 4, 4, 100, all inside the window, so all three are drawn.

**Case 1: non-rotated sprite at the origin, centre case.** pos = (0,0,0,1), size = (w 8, h 6, 0, 0), mode 0.

- Pclip = col3 = (8192, 8192, 40, 4). Pw = 4, Q = 0.25.
- c = (8192*0.25, 8192*0.25, 40*0.25, 0.25) = (2048, 2048, 10, 0.25). The centre lands at 2048 = the screen centre implied by `persp[2][0..1] = 2048` (`mg_drawenv.cpp:229`): for any camera that puts the origin on the optical axis, `Px/Pw = 2048`.
- half.x = 8 * 2 * 0.5 = 8; half.y = 6 * 2 * 0.5 = 6. hq = (8*0.25, 6*0.25) = (2, 1.5).
- min = (2046, 2046.5, 10, 0.25); max = (2050, 2049.5, 10, 0.25).
- Fog: F0 = 0.5 + 8*0.25 = 2.5; min(2.5, 1.0) = 1.0; max(1.0, 0.0) = 1.0. F = 1.0.
- Cull: min.x 2046 > 1 ✓; min.y 2046.5 > 1 ✓; max.x 2050 < 4095 ✓; max.y 2049.5 < 4095 ✓; s22.w = 1/200 - 0.25 = -0.245 < 0 ✓ (Pw < 200); s23.w = 0.25 - 1 = -0.75 < 0 ✓ (Pw > 1). Drawn, ADC = 0.
- Output (N = 1), GIF packet quadwords (out[k] = vi13 + k): out[0] = tag (0x8001, TOPS[0].yzw); out[1] = RGBAQ (0x80, 0x80, 0x80, 0x60); out[2] = UV0 (0, 0); out[3] = XYZF2 v0; out[4] = UV1 (256, 256); out[5] = XYZF2 v1.
- v0: X = trunc(16 * 2046) = 32736 = 0x7FE0; Y = trunc(16 * 2046.5) = 32744 = 0x7FE8; Z = trunc(10) = 10; w word = trunc(16 * 1.0) = 16 = 0x0010 (F = 1, ADC = 0).
- v1: X = trunc(16 * 2050) = 32800 = 0x8020; Y = trunc(16 * 2049.5) = 32792 = 0x8018; Z = 10; w word 0x0010.

**Case 2: mode 1 (rotated) sprite, rotated by 90 degrees.** pos = (0,0,0,1), size = (w 8, h 6, sin = 1, cos = 0). Same LS, VS, fog, bounds as case 1.

- Centre as case 1: c = (2048, 2048, 10, 0.25).
- A = (cos*2, sin*2) * (w/2 = 4) = (0, 8); A' = A*Q = (0, 2).
- B = (-sin*2, cos*2) * (h/2 = 3) = (-6, 0); B' = B*Q = (-1.5, 0).
- A'+B' = (-1.5, 2); B'-A' = (-1.5, -2).
- v0 = c - (A'+B') = (2049.5, 2046); v1 = c - (B'-A') = (2049.5, 2050); v2 = c + (B'-A') = (2046.5, 2046); v3 = c + (A'+B') = (2046.5, 2050).
- Extents: x in [2046.5, 2049.5] (3 px), y in [2046, 2050] (4 px). The rotation swaps the axes: the 8-unit size now runs along y (2 units each side, 4 px total) and the 6-unit height runs along x (1.5 units each side, 3 px total).
- Fog F = 1.0 (as case 1, Pw = 4). Cull: min = (2046.5, 2046), max = (2049.5, 2050): all pass; drawn, so v2 and v3 ADC = 0. v0 and v1 always ADC (P:0121-0122).
- Output (N = 1, NREG 9): out[0] = tag (0x8001, TOPS[0].yzw); out[1] = RGBAQ (0x80, 0x80, 0x80, 0x60); out[2] = UV (0, 0); out[3] = XYZF2 v0; out[4] = UV (256, 0); out[5] = XYZF2 v1; out[6] = UV (0, 256); out[7] = XYZF2 v2; out[8] = UV (256, 256); out[9] = XYZF2 v3.
- v0: X = trunc(16 * 2049.5) = 32792 = 0x8018; Y = trunc(16 * 2046) = 32736 = 0x7FE0; Z = 10; w word = trunc(16 * (1.0 + 2048)) = 32784 = 0x8010 (ADC = 1, F = 1), since v0 always gets the +2048.
- v1: X = 0x8018; Y = trunc(16 * 2050) = 32800 = 0x8020; Z = 10; w word 0x8010 (ADC = 1, F = 1), since v1 always gets the +2048.
- v2: X = trunc(16 * 2046.5) = 32744 = 0x7FE8; Y = 0x7FE0; Z = 10; w word 0x0010 (drawn, no ADC).
- v3: X = 0x7FE8; Y = 0x8020; Z = 10; w word 0x0010.

**Case 3: fog clamped at the lower bound.** pos = (0,0,0,1), size (8, 6, 0, 0), mode 0, but Pw = 100, with far_value = 0.8 (data 0x3B.z = 0.8; other values as common).

- Pclip = (2048*100, 2048*100, 1000, 100) = (204800, 204800, 1000, 100). Q = 0.01. c = (2048, 2048, 10, 0.01).
- hq = (8*0.01, 6*0.01) = (0.08, 0.06). min = (2047.92, 2047.94, 10, 0.01); max = (2048.08, 2048.06, 10, 0.01).
- Fog: F0 = 0.5 + 8*0.01 = 0.58. min(0.58, near_value 1.0) = 0.58. max(0.58, far_value 0.8) = 0.8. Clamped to far_value; F = 0.8.
- Cull: min.x 2047.92 > 1 ✓; min.y ✓; max.x 2048.08 < 4095 ✓; max.y ✓; s22.w = 1/200 - 0.01 = -0.005 < 0 ✓; s23.w = 0.01 - 1 = -0.99 < 0 ✓. Drawn.
- Output: RGBAQ as case 1. UV as case 1. v0: X = trunc(16*2047.92) = trunc(32766.72) = 32766 = 0x7FFE; Y = trunc(16*2047.94) = trunc(32767.04) = 32767 = 0x7FFF; Z = 10; W = trunc(0.8*16)=12 (F field = trunc(0.8) = 0; w word = 0x000C). v1: X = trunc(16*2048.08) = 32769 = 0x8001; Y = trunc(16*2048.06) = 32768 = 0x8000; Z = 10; W = 0x000C.

Without the clamp the value would be 0.58 (w word 9, F = 0) and the F field is the same (0), so this case shows the clamp only in the w word's fraction bits. A case where the F field differs: far_value = 2.5 gives F = 2 (clamped) against an unclamped 0.58 (F = 0). For that vector: F = trunc(2.5) = 2, w word = 40 (0x0028). Mention: the clamp changes the F field from 0 to 2.

Clamp check at upper bound (for completeness): Pw = 1.1 gives F0 = 0.5 + 8/1.1 = 7.77, min(7.77, 1.0) = 1.0 (clamped to near_value). Not computed further.

---

## 11. Registers inherited from Vu_progmain (and memory it leaves behind)

Vu_progmain's `__main` (`M:0006-0053`), run by MSCAL 0 at the start of each render packet, leaves the following state for this program. Every batch of the packet runs with these values.

| Register or memory | Value | Set at (progmain pair) | Read by this program at |
|---|---|---|---|
| vf10 | LS column 0 (data 4) | `M:0015` (`lqi (vi01++)`, vi01 = 4 from `M:0014`) | P:0098, P:00B2, P:00F4 |
| vf11 | LS column 1 (data 5) | `M:0016` | P:0099, P:00B4, P:00F5 |
| vf12 | LS column 2 (data 6) | `M:0019` | P:009A, P:00B5, P:00F6 |
| vf13 | LS column 3 (data 7) | `M:001A` | P:009B, P:00B8, P:00F7 |
| vi13 | 420 | `M:001F` (`iaddiu vi13, vi00, 420`) | P:0068 (first batch toggles to 600) |
| vf00 | (0,0,0,1) | hardware | many |
| data 21 (loaded into vf31 by `lq.xyzw vf31, 21(vi00)` at P:0088; rot P:00DF) | (x, y, guard_max.z, guard_max.w = far). xy = (4095, 4095) with SCISSOR clear, else guard_max.xy. w is always the 0x39.w value | xy `M:004D` (`addi.xy vf15`); w `M:0046` (lq into vf15) and `M:004E` (sq) | P:0088 |
| data 22 (loaded into vf30 by `lq.xyzw vf30, 22(vi00)` at P:0089; rot P:00E0) | (x, y, guard_min.z, guard_min.w = near). xy = (1, 1) with SCISSOR clear, else guard_min.xy. w is always the 0x3A.w value | xy `M:004C` (`addw[I].xy vf16`); w `M:0047` (lq into vf16) and `M:004F` (sq) | P:0089 |

Notes:

- vf10-vf13 are the only registers the program reads before setting them that carry a real CPU-derived value. They persist from the MSCAL 0 of the same render packet.
- The program writes vf15, vf16 (P:0083-0085 uses vf15; P:008E-008F write vf16/vf17), so Progmain's vf15/vf16 values are not used directly, only through data 21/22.
- Progmain's other outputs are not used by this program: vi01, vi02, vi07, vi08, vi09, vi10, vi11, vi12, vi14, vi15 are all set by this program before use; vf01-vf09, vf14, vf18-vf21, vf26, vf27, vf31 are overwritten or not read.
- The ACC, Q and I registers are set before use by this program (ACC by its first `mulax`, Q by the first `div`, I by `loi`).
- Data 0x3B (fog) and 0x39/0x3A (guard) are not Progmain outputs: `mgFlushRenderInfo` writes them, and Progmain only copies 0x39/0x3A into vf15/vf16 (section 3).
- The `ibne` at `M:004A` skips `M:004C-004D` when SCISSOR is set, so the xy of data 21/22 become (4095, 4095) and (1, 1) only when SCISSOR is clear. The `sq` at `M:004E`/`M:004F` run in both cases, and the w of both is always the guard value (data 0x39.w = far, 0x3A.w = near). With SCISSOR clear (always, section 3), the xy cull bounds are (1, 4095), and the w window does not depend on SCISSOR.

Apart from vf10-vf13 and vi13 (section 11), no register is read before this program sets it. Every other read in `prog_3dsp.txt` follows a write in the same program at the pair shown in section 2.
