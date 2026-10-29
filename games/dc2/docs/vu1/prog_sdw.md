# Vu_prog_sdw (VU1)

Shadow-volume microprogram. For each batch of up to 42 MDT triangles the CPU sends object-space vertex positions. Per triangle the program computes object-to-clip positions C_j = V_j·VC and shadow-projected positions T_j = V_j·SM, rejects the triangle if any C_j or T_j lies outside the |w| frustum or if the facing value |Q| <= 1e-4, and otherwise extrudes it into five 4-vertex quads: three side quads, one cap of the original triangle and one cap of the projected triangle. Winding is chosen by the sign of Q (lit or unlit), so the volume is consistently oriented. Each quad is divided by w, scaled to 12.4 GS coordinates and classified front or back by the sign of its screen-space signed area. Front quads are sent with the ADD alpha state and back quads with the SUB state, so the frame buffer counts shadow-volume entries and exits. Output is double-buffered between VU addresses 420 and 600.

Labels (listing `port/build/vu1/prog_sdw.txt`, micro pair addresses): `__INIT_DATA__2` 0x064, `__LPpLSl4t1__2` 0x06C, `__NXpLSl4t1__2` 0x06D, `__MAIN_PROG__2` 0x078, `__LPmain__2` 0x08E, `__NXshadow1` 0x0E9, `__NXshadow2` 0x0FD, `__SKshadow` 0x0FF, `__ENDmain__2` 0x103, `__OUTPUTS` 0x109, `__LPoutputs` 0x126, `__NXoutputs` 0x15B, `__NXoutputs1` 0x165, `__LPoutputs1` 0x16F, `__SKoutputs1` 0x175, `__LPoutputs2` 0x180, `__ENDoutputs` 0x188. Last pair 0x18C. Program body 0x064-0x18C (MPG 256 pairs at 0x064, then 41 pairs at 0x164).

Notation: `v.k` = component k of v. `[k]` = row k of a 4x4 matrix stored as four qwords. `A·M` is the row-vector product (v·M = sum over j of v.j * M[j]). `S_i` = screen vertex i after divide. Integer values are in 12.4 unless stated. `(inferred)` marks anything not settled by the listing and repo.

---

## 1. Role and entry points

Upload: `mgSendVuProg(MG_VU_PROG_SHADOW)` in each face chain (`ps2/src/mg_visual.cpp:643`, `mg_shadow.cpp:190`) loads this program to micro 0x064. Vu_progmain stays at micro 0x00-0x54 and is never overwritten (`cpu_feed_other.md` 0.3).

| Entry | Issued by | What runs |
|---|---|---|
| MSCAL 0 | render-info packet, `mg_shadow.cpp:339` | Vu_progmain 0x0000: `bal vi15, __main` (0x0000), __main 0x0006-0x0053, `jr vi15` at 0x0053 with E on its delay slot 0x0054. Vu_progmain halts; the next MSCAL 2 starts at 0x0002. The shadow code does not run in this MSCAL. |
| MSCAL 2 | every face batch, `CreateFacePacket` `prog_vif` (`mg_shadow.cpp:55`, `:129`) | 0x0002 `iaddiu vi01, vi00, 100`; 0x0004 `jr vi01` (delay slot 0x0005) jumps to 0x0064 `__INIT_DATA__2`. |
| Halt / resume | batch end, 0x0073 `bal __MAIN_PROG__2` returns to 0x0075, which is `nop[E] \| b __INIT_DATA__2` | Halt after the delay-slot pair 0x0076. Resume PC = 0x0064 (branch target). |

Halt PC is irrelevant here. The shadow path emits only MSCAL 0 (`mg_shadow.cpp:339`) and MSCAL 2 (`:55`, `:129`); no MSCNT is sent for this program (MSCNT appears only in the other programs' paths, `mg_visual.cpp:1064`, `mg_sprite.cpp:381`, `visualmotion.cpp:533`, `water.cpp:658`). So the resume PC of the halt is never used.

Per batch: 0x064-0x072 setup, 0x073 `bal` into the triangle loop (0x078-0x101), `ENDmain` 0x103-0x107 pops the return link and `jr`, 0x075 halts. Each emitted triangle also calls `__OUTPUTS` (0x109-0x18B) from 0x0FD.

Persistence across halts and across draws: all vf/vi registers, the VU data memory and the ACC/Q/I state persist. The sdw code reads only registers it has set itself, except the inherited values listed in 2.3.

---

## 2. Register conventions

### 2.1 Integer registers

| Reg | Value | Set at | Notes |
|---|---|---|---|
| vi00 | 0 | constant | |
| vi12 | TOPS: qw address of the current batch | `xtop` 0x064 | Batch layout 3.2. |
| vi13 | output base B (420 = 0x1A4 or 600 = 0x258) | 0x068-0x06C | Toggles every batch: if the previous value was 420 then B = 600, else B = 420. Persists across batches and draws. Initial value is stale (inferred). |
| vi14 | stack pointer | 0x065 = 1024 each batch | Stack pushes at 1023 (0x079, `__MAIN`) and 1022 (0x109, `__OUTPUTS`). |
| vi15 | return link | `bal` 0x073 (-> 0x075), `bal` 0x0FD (-> 0x0FF) | Saved to stack at 0x079 and restored at 0x103. |
| vi11 | triangles remaining | 0x06E (= n) | Decremented at 0x0FF, loop test 0x101. |
| vi08 | input pointer P (triangle k at P = TOPS+2+3k) | 0x081 (= TOPS+2) | Advanced by 3 at 0x100. Saved at 0x10B, restored at 0x188. Inside `__OUTPUTS` it is reused as scratch: 0x116 sets it to 3 (dead store), 0x14B overwrites it with the back flag (2.4). |
| vi06 | scratch pointer (800) | 0x09B per triangle, 0x113 in `__OUTPUTS` | Writes in 0x0D3-0x0E6 or 0x0E9-0x0FC; reads in `__LPoutputs` 0x126-0x129. |
| vi02 | front-packet count F | 0x118 = 0 per triangle | See 2.4. Saved at 0x10A, restored at 0x189. |
| vi03 | back-packet count B | 0x119 = 0 per triangle | See 2.4. |
| vi04 | front output pointer | 0x10D and 0x114 = B | See 2.4. |
| vi05 | back output pointer | 0x115 / 0x17B = B+90 | See 2.4. |
| vi07 | sign-mask scratch | 0x0C3, 0x0C6, 0x147 | `vi07 = 128` (x sign) at 0x0C3; `vi07 = 32` (z sign) at 0x147 and 0x14A; `vi07 = 0x8004` (tag word) at 0x10E-0x10F. |
| vi09 | 44 (0x2C) | 0x123 | XGKICK address for the ADD state packet. |
| vi10 | group counter (5), then 44 / 46 | 0x11A (5), 0x16C (44), 0x177 (46) | 46 = 0x2E, the SUB state packet. |

Batch-start pointers (vi04, vi05, vi06, vi07 at 0x06F-0x072) are overwritten before use. They are dead.

### 2.2 Vector registers (set in this program)

| Reg | Value | Set at |
|---|---|---|
| vf00 | (0,0,0,1) | constant |
| vf21 | (-0.1, -0.1, -0.1, -0.1) | 0x082 (zero), 0x087 (add I = -0.1) |
| vf22 | L_clip (see 5.2) | 0x08D |
| vf23 / vf24 / vf25 | T0 / T1 / T2 (projected positions) | 0x097 / 0x09B / 0x09F |
| vf15 / vf16 / vf17 | C0 / C1 / C2 (object-to-clip, overwrite V0..V2) | 0x0A4 / 0x0A8 / 0x0AC |
| vf18 / vf19 | E1 = C1-C0, E2 = C2-C0 | 0x0B0 / 0x0B1 |
| vf20 | E2×E1 (xyz, exact by ruling d), then the facing value Q in x, then 1e-4 in w | 0x0B9-0x0BA, 0x0BE-0x0C2, 0x0C8 |
| vf26-vf29 | VC rows (0x082-0x085 for the light, 0x09D-0x0A0 for C), SM rows (0x090-0x093), CS rows (0x11F-0x122) | varies, see 3.1 |
| vf02.x / vf03.x / vf04.x | Â_0, Â_1, Â_2 (object-space unit light vector) | **inherited from Vu_progmain** (2.3) |
| Q | 1/w from `div` (0x131, 0x135, 0x139, 0x13E) | 0x131-0x145 |
| I | -0.1 (0x086), 1e-4 (0x0C7), 1.0 (0x117), 64.0 (0x11C) | `loi` |

All VF data flow in this program is sequential (VF hazards are interlocked). The one place that depends on Q timing is the `mulq` reads after each `div` in 5.4.

### 2.3 Inherited from Vu_progmain (read by sdw)

| Inherited | Read at | Written by | Value |
|---|---|---|---|
| vf02.x = Â_0 | 0x088 (`mulx.x vf23, vf21, vf02x`) | Vu_progmain 0x0006-0x0022 (MSCAL 0 of the same draw) | normalised (R0·L) |
| vf03.x = Â_1 | 0x089 | Vu_progmain 0x0023-0x0028 | normalised (R1·L) |
| vf04.x = Â_2 | 0x08A | Vu_progmain 0x0024-0x0029 | normalised (R2·L) |

Other values from Vu_progmain are never read by sdw. Vu_progmain writes VU 0x1E.x, 0x15, 0x16 and 0x2C. VU 0x1E.x is read by sdw as the x component of CS row 1, which holds 0.0 in both cases (7.3). VU 0x15/0x16 and 0x2C are overwritten or unused for sdw (see 3.1).

### 2.4 Output counters and pointers in `__OUTPUTS` (trace 0x109-0x18B)

| Reg | Set | Changed | Read | Meaning |
|---|---|---|---|---|
| vi02 | 0x118 = 0 (saved at 0x10A) | +1 at 0x15A (front path, delay slot of `b` at 0x159); -1 per iteration of `__LPoutputs1` (0x16F, F times) | 0x16A (== 0: skip to 0x188); 0x172 loop test | F = front quads of this triangle, 0..5 |
| vi03 | 0x119 = 0 | +1 at 0x164 (back path only, after the data stores 0x15B-0x163); -1 per iteration of `__LPoutputs2` (0x181, B times) | 0x175 (== 0: skip SUB block); 0x185 loop test | B = back quads of this triangle, 0..5; F + B = 5 |
| vi04 | 0x10D = B (tag store at B+0, 0x110-0x111, 0x112 reload); 0x114 = B | +9 per front packet (0x158); 0x169 = B; +9 per `__LPoutputs1` iteration | front stores 0x14E-0x156 and XGKICK 0x157 | front packet base B+9i. After 0x169 nothing reads it except the dead increments in `__LPoutputs1`. |
| vi05 | 0x115 = B+90 | +9 per back quad (0x163); 0x17B = B+90 (reset); +9 per `__LPoutputs2` iteration (0x182) | back data stores 0x15B-0x162 (at vi05+1..+8); tag stores 0x17C, 0x184; XGKICK 0x180 | back packet base B+90+9j |
| vi10 | 0x11A = 5 | -1 per group (0x165); 0x16C = 44; 0x177 = 46 | group loop test 0x167; XGKICK operand 0x174 (44), 0x179 and 0x187 (46) | group counter, then the state-packet address |
| vi06 | 0x113 = 800 | +1 per `lqi`/`sqi` | 0x126-0x129 | scratch read pointer; 20 qw read per triangle |
| vi08 | saved at 0x10B | 0x116 = 3 (dead store); 0x14B = (vi07 & MAC flags) = 0 or 32 | 0x14C branch (== 32: back); restored at 0x188 | back flag for the current quad |

Per-group flow: the quad's z-sign flag (from the opmsub at 0x147, read by fmand at 0x14B) picks the front path (0x14E-0x158, F += 1) or back path (0x15B-0x164, B += 1). Each of the five groups takes exactly one path.

---

## 3. Data memory map

### 3.1 Per-draw block (written by the render-info packet at MSCAL 0, read via qw addresses)

Writers: `CreateRenderInfoPacket` (`ps2/src/mg_shadow.cpp:285-389`) and `SetShadowData` (`:19-47`). Addresses are VU qw; the CPU head index minus 2 gives the VU address (cpu_feed A.3).

| VU qw | Contents | Writer (CPU line) | Readers (VU) |
|---|---|---|---|
| 0x00-0x02 | zero | 302-306 | none |
| 0x03 | render_params[3] (raw int) in x; render_params[0..2] in y,z,w | 307-310 | none |
| 0x04-0x07 | world_screen = matrix·world_screen (4 rows) | 288, 311 | __main loads to vf10-vf13; unused |
| 0x08-0x0B | object matrix rows R0..R3 | 312 | __main 0x0009-0x0F (R0..R2 xyz used) |
| 0x0C | (L.x, 0, 0, 0) | 313, 316 | __main 0x0A (vf19) |
| 0x0D | (L.y, 0, 0, 0) | 314, 317 | __main 0x0A (vf20) |
| 0x0E | (L.z, 0, 0, 0) | 315, 318 | __main 0x0C (vf21) |
| 0x0F-0x14 | not written by CPU (stale) | none | __main loads 0x0F-0x13 into vf05-vf09; unused |
| 0x15 | full_max (CPU) | 319 | **overwritten** by __main 0x04E `sq vf15, 21(vi00)` (guard xy = (4095,4095) or from VU 0x39). sdw does not read 0x15. |
| 0x16 | full_min (CPU) | 320 | **overwritten** by __main 0x04F. sdw does not read 0x16. |
| 0x17 | not written (stale) | none | none |
| 0x18 | not written (stale) | none | __main 0x017 loads vf25; that value feeds the dead store at 0x020 |
| 0x19-0x1C | VC = matrix·view·view_clip_full (4 rows) | 324-329 | sdw 0x082-0x085, 0x09D-0x0A0 |
| 0x1D | CS row 0 = clip_screen_full[0] | 330 | sdw 0x11F |
| 0x1E | CS row 1 = clip_screen_full[1] = (0, full_scale, 0, 0) (CS[1][0] = 0.0, `mg_drawenv.cpp:209-223`) | 331 | sdw 0x120. The x word is overwritten by __main 0x042 `isw.x vi07, 30(vi00)` with the integer 0 or 32, which reads back as 0.0 either way (7.3). |
| 0x1F | CS row 2 = clip_screen_full[2] | 332 | sdw 0x121 |
| 0x20 | CS row 3 = clip_screen_full[3] = (2048, 2048, z-offset, 1) (`mg_drawenv.cpp:217-219`) | 333 | sdw 0x122 |
| 0x21-0x25 | unused | none | none |
| 0x26 | draw flags (sprite / water only) | not written for shadows | __main 0x044 (`ilw.x vi01, 38`); the value is stale. |
| 0x28-0x2B | SM = matrix·shadow·view·view_clip_full (4 rows) | `SetShadowData` (`mg_shadow.cpp:24`, UNPACK NUM 8 at 0x28) | sdw 0x090-0x093 |
| 0x2C | GIF tag: NLOOP 1, EOP 1, NREG 1, packed A+D (lo 0x8001, hi 0x102E8000 / 0xE) | `SetShadowData` (data words 0x8001, 0x102E8000, 0xE, 0) | XGKICK at 0x125 and 0x174 (cpu_feed A.4). __main 0x020 stores vf27 here during MSCAL 0; the UNPACK comes later in the stream (9.2). |
| 0x2D | A+D: ALPHA_1 = 0x68 (additive), FIX = 0x80 | `SetShadowData` | XGKICK packet (0x2C) |
| 0x2E | GIF tag as 0x2C | `SetShadowData` | XGKICK at 0x179 and 0x187 |
| 0x2F | A+D: ALPHA_1 = 0x62 (subtractive), FIX = 0x80 | `SetShadowData` | XGKICK packet (0x2E) |
| 0x30-0x38 | unused | none | none |
| 0x39, 0x3A | not written by any shadow CPU path | none | __main 0x046-0x047 (guard qwords). Not read by sdw. |

Decoding the ALPHA values (GS ALPHA: A,B in {Cs,Cd,0}, C in {As,Ad,FIX}, D in {Cs,Cd,0}, 2-bit fields at bits 1:0, 3:2, 5:4, 7:6):
- 0x68: A=Cs, B=0, C=FIX, D=Cd: `Cd' = Cs·FIX/128 + Cd` (additive, FIX/128 = 1).
- 0x62: A=0, B=Cs, C=FIX, D=Cd: `Cd' = Cd - Cs·FIX/128` (subtractive).

### 3.2 Per-batch input (FLG=1 UNPACK, TOPS-relative, 3n+2 qw)

Writer: `CreateFacePacket` (`mg_shadow.cpp:54-156`). UNPACK V4_32 NUM = 3n+2 at address 0, FLG=1 (`0x6C008000`, line 128). Max n = 42 (line 98), so NUM <= 128.

| TOPS+ | Contents | Writer (line) | Reader (VU) |
|---|---|---|---|
| +0 | GIF tag: NLOOP = n, EOP 1, PRE 1, PRIM = TRIANGLE_FAN with args (5, 1, 1, 0, 1, 0, 0, 0, 0), NREG 2, REGS = RGBAQ, XYZF2 | 82-93, 111-112 | 0x10C loads it into vf21, the template for every output tag (6.1). The 0x07D `lq` copies it to B+0 (0x07F) only as a store that each triangle overwrites. |
| +1 | header {n, n, type, (word 3 unwritten)} | 113-115 | 0x067 (ilw.y -> vi02 = n), 0x06D (ilw.x -> vi01 = n) |
| +2+3k+j | vertex j (j = 0..2) of triangle k: float4 object-space position (index = MDT vertex index, `CreateFace` 170-180) | 117-126 | 0x08E, 0x094, 0x098 (V0,V1,V2 loads) |
| +2+3n | VIF: MSCAL 2 (not in the UNPACK; `prog_vif`) | 55, 129 | VIF only |

Triangle k uses TOPS+2+3k .. TOPS+4+3k. TOPS is 60 (0x3C) or 180 (0xB4) (BASE/OFFSET), see 3.3.

### 3.3 TOPS and BASE/OFFSET

BASE and OFFSET are written by the render-info packet as `vu1_base` and `vu1_offset` (`mg_shadow.cpp:300-301`). A shadow object's values are BASE = 60 and OFFSET = 180 (`mg_visual.cpp:341-342`, inside `mgCVisualMDT::Initialize`, called from the constructor `mgCVisualMDT()` in `mg_visual.hpp`; `mgCShadowMDT` adds no override). The 0 assignments at `mg_visual.cpp:339-340` are overwritten in the same function. So batches land at TOPS = 60 or 180, never 0.

Overlap check against the per-draw block and other areas (TOPS in {60, 180}): batch data covers TOPS..TOPS+3n+1 and never meets the per-draw block (0x00-0x20), SM (0x28-0x2B), 0x2C-0x2F, 0x39/0x3A, or the output (420+). It does not meet the scratch (800+) or the stack (1022-1023).

Overlap between the two batch buffers: the buffers are 60..179 and 180..307, but a batch with n >= 40 extends past 179 (n = 42 reaches 187). So for n >= 40 the batch in the 60 buffer overlaps the first qws of the 180 buffer (180..187 for n = 42). See open point 17.

### 3.4 Program-written buffers

| VU qw | Contents | Written at | Read at |
|---|---|---|---|
| B = 420 or 600 (vi13) | front packets: 9 qw each at B+9i, i = 0..4 (tag, then RGBAQ/XYZF2 pairs) | 0x14E-0x156 (vi04) | XGKICK 0x157 (immediately) |
| B+90 .. B+134 | back packets: 9 qw each at B+90+9j. Data at +1..+8 (0x15B-0x162), tag at +0 (0x17C, 0x184) | 0x15B-0x163, 0x17C, 0x184 (vi05) | XGKICK 0x180 (vi03 times) |
| 800-819 (0x320-0x333) | per-triangle scratch: 20 qw of clip-space vertices (sqi order in 5.3) | 0x0D3-0x0E6 or 0x0E9-0x0FC (vi06) | 0x126-0x129 (vi06 from 800) |
| 1022 | OUTPUTS push: x = vi02 (saved), y = vi08 (saved) | 0x10A-0x10B | 0x188-0x189 |
| 1023 | MAIN push: x = vi15 (return to 0x075) | 0x079 | 0x103 |

Front and back regions are reset per triangle, so every triangle in a batch reuses B and B+90. This is only safe if XGKICK reads its source before the next triangle overwrites it (open point 11).

---

## 4. Control flow

### 4.1 Call graph

```
MSCAL 2 -> 0x02 -> jr -> __INIT_DATA__2 (0x064)
  xtop, vi14 = 1024, vi13 toggle, read n, pointers
  bal vi15 -> __MAIN_PROG__2 (0x078)              [link 0x075]
    push vi15
    loop over triangles __LPmain__2 (0x08E .. 0x101)
      per triangle: bal vi15 -> __OUTPUTS (0x109)  [link 0x0FF] (only if not rejected)
        ... jr vi15 (0x18B) back to 0x0FF (__SKshadow)
    __ENDmain__2 (0x103): pop vi15; jr vi15 -> 0x075
  0x075: nop[E] | b __INIT_DATA__2  -> halt, resume 0x064
```

### 4.2 Branches (address, condition, effect)

| Addr | Instruction | Taken when | Effect |
|---|---|---|---|
| 0x068 | `ibeq vi13, vi01(420)` | vi13 == 420 | to 0x06C: vi13 = 600. Not taken: 0x06B sets vi13 = 420. |
| 0x0B4 | `ibne vi01, 0 -> SKshadow` (after fcand 0x3FFFF on C flags) | any C_j outside (6 bits per vertex) | triangle skipped |
| 0x0BD | `ibne` (after fcand 0x3FFFF on T flags) | any T_j outside | skipped |
| 0x0CE | `ibeq vi01, 0 -> SKshadow` (after fcand 0x3 on the Q test) | Q test bits 0,1 clear, i.e. \|Q\| <= 1e-4 | skipped |
| 0x0D1 | `ibeq vi07, 0 -> NXshadow1` | vi07 == 0, i.e. Q >= 0 (sign flag clear) | path A (0x0E9); else path B (0x0D3) |
| 0x101 | `ibne vi11, 0 -> LPmain` | triangles remain | next triangle |
| 0x14C | `ibeq vi08, vi07(32) -> NXoutputs` | z-sign flag set (screen area negative) | back packet path (0x15B) |
| 0x16A | `ibeq vi02, 0 -> ENDoutputs` | F = 0: no front packets for this triangle | **skips all back packets and the SUB state** (6.5, open point 6) |
| 0x172 | `ibne vi02, 0 -> LPoutputs1` | front count not zero | counting loop, no XGKICK (open point 7) |
| 0x167 | `ibne vi10, 0 -> LPoutputs` | groups remain (5) | next group |
| 0x175 | `ibeq vi03, 0 -> ENDoutputs` | B = 0: no back packets | skip SUB block |
| 0x185 | `ibne vi03, 0 -> LPoutputs2` | back packets remain | next back XGKICK |

Loop counters: vi11 = n (triangles), vi10 = 5 (quads per triangle), vi03 = B (back count), vi02 = F (front count). F + B = 5 for every triangle that reaches `__OUTPUTS` (each of the 5 groups takes exactly one of the two paths).

### 4.3 Per-triangle path

1. Load V0..V2 (0x08E-0x098), SM (0x090-0x093 per triangle), compute T_j (0x094-0x09F).
2. Load VC (0x09D-0x0A0), compute C_j (0x0A1-0x0AC).
3. Clip test on C (0x0AD-0x0B4). Reject if any of 18 flag bits.
4. E1, E2, then E2×E1 (0x0B0-0x0BA).
5. Clip test on T (0x0B6-0x0BD). Reject if any.
6. Q = sum over xyz of vf20 · vf22 (0x0BE-0x0C2). Test |Q| <= 1e-4 (0x0C7-0x0CE). Reject if not larger.
7. Sign flag of Q -> path A (Q >= 0) or path B (Q < 0) (0x0C6, 0x0D1).
8. Emit 20 qw into scratch 800-819 (0x0D3-0x0E6 or 0x0E9-0x0FC).
9. `bal __OUTPUTS` (0x0FD). `__OUTPUTS` builds and kicks the five quads (see 6).
10. Advance P by 3 (0x100), decrement n (0x0FF), loop (0x101).

---

## 5. Per-element math

### 5.1 Per draw (Vu_progmain, inherited; see 2.3)

```
R_i = VU[0x08+i].xyz      (object matrix rows, i = 0..2)
L   = (VU[0x0C].x, VU[0x0D].x, VU[0x0E].x)      (light, as stored by CPU)
A_i = dot(R_i, L)                                  (vf02.x, vf03.x, vf04.x; 0x009-0x016)
Ahat = A / |A|                                     (0x017-0x02D; Q from rsqrt(|A|^2))
det  = dot(cross(R0, R1), R2)                      (0x02E-0x034; vf24.x = det exactly, ruling d)
hflag = (det < 0) ? 32 : 0                         (0x038-0x042; stored to VU 0x1E.x, read as 0.0, see 7.3)
```

### 5.2 Light in clip space (per batch)

```
D_r  = -0.1 * Ahat_r                    r = 0..2   (vf23.x,y,z; 0x087-0x08A)
Lc   = sum over r=0..2 of D_r * VC[r]              (vf22; 0x08B-0x08D)   [4 comps; only xyz used]
```

### 5.3 Per triangle (inputs V0, V1, V2 = object-space float4)

```
T_j = V_j · SM                     j = 0..2     (vf23, vf24, vf25; 0x094-0x09F)
C_j = V_j · VC                     j = 0..2     (vf15, vf16, vf17; 0x0A1-0x0AC)

// clip flags of C_j (xyz vs |w|), then of T_j (after 0x0B6-0x0B8)
E1 = C1.xyz - C0.xyz                              (vf18; 0x0B0)
E2 = C2.xyz - C0.xyz                              (vf19; 0x0B1)
N  = cross(E2, E1)                                 (vf20.xyz; 0x0B9-0x0BA, exact by ruling d)
Q  = N.x*Lc.x + N.y*Lc.y + N.z*Lc.z               (vf20.x; 0x0BE-0x0C2)
// reject if Q-test bits clear: |Q| <= 1e-4       (w of vf20 = 1e-4; 0x0C8-0x0CD)
pathB = (Q < 0)                                   (sign flag, 0x0C6)
```

The cross product is computed by `opmula` / `opmsub`: `opmula ACC, vf19, vf18` (0x0B9) sets ACC = (E2.y*E1.z, E2.z*E1.x, E2.x*E1.y), and `opmsub vf20, vf18, vf19` (0x0BA) gives vf20 = ACC - (E1.y*E2.z, E1.z*E2.x, E1.x*E2.y) = E2×E1 exactly (ruling d, not twice it). So N = cross(E2,E1) with no factor 2.

Path B (Q < 0), 20 scratch qw in this order (0x0D3-0x0E6):

```
[C0, C1, T1, T0 |  C1, C2, T2, T1 |  C2, C0, T0, T2 |  T0, T1, T2, T2 |  C2, C1, C0, C0]
```

Path A (Q >= 0), 20 scratch qw (0x0E9-0x0FC):

```
[C0, T0, T1, C1 |  C1, T1, T2, C2 |  C2, T2, T0, C0 |  T2, T1, T0, T0 |  C0, C1, C2, C2]
```

Groups of 4 are quads Q_g = (P0, P1, P2, P3). Path A is path B with each quad reversed. Groups 1-3 are side quads, group 4 is a cap (its fourth vertex repeats), group 5 is the other cap.

### 5.4 Output projection (per group, `__LPoutputs` 0x126-0x145)

```
P_i   = Q_g.vertex_i · CS             CS rows = VU 0x1D..0x20  (vf26..vf29)
w'_i  = P_i.w
S_i   = P_i / w'_i                    (div at 0x131/0x135/0x139/0x13E, mulq; w ends as 1.0)
X_i   = trunc(16 * S_i.x)             (ftoi4, 12.4)
Y_i   = trunc(16 * S_i.y)
Z_i   = trunc(16 * S_i.z) >> 4        (24-bit field, bits 27..4 of the z word)
F_i   = (trunc(16 * S_i.w) >> 4) & 0xFF   (fog byte; see open point 16)
ADC   = 0
RGBAQ = (1, 1, 1, 64)                 (vf25 via ftoi0, 0x113-0x11E)
```

Each `mulq ..., Q` is placed 7 pairs after the `div` that produced its Q (0x131 -> 0x138, 0x135 -> 0x13C, 0x139 -> 0x140, 0x13E -> 0x145). Each read therefore sees the new Q, provided the DIV latency is at most 7 pairs (inferred; the latency is not in the repo). No `waitq` is used in this program.

Front/back sign (0x141-0x147):

```
E1s = S1.xy - S0.xy ;  E2s = S2.xy - S0.xy        (vf19, vf20; 0x141, 0x144)
zsign = E2s.x * E1s.y - E2s.y * E1s.x            (vf20.z after opmula/opmsub, 0x146-0x147; exact by ruling d)
back  = (zsign < 0)                                 (MAC z sign flag, read by fmand at 0x14B, vi08 = 32; 0x14B-0x14C)
```

`back` is taken on the post-division floats, before ftoi4. Only S0, S1, S2 are used; the second triangle of the fan (S0,S2,S3) is not checked. A zero sign (degenerate quad) counts as front. The MAC flag read at 0x14B is 4 pairs after the opmsub at 0x147, which is the padding the code relies on (flags are not interlocked; machine facts). The opmsub writes .xyz, so its Z sign flag is the sign of cross.z (ruling c).

---

## 6. Output

### 6.1 GIF packets

Each output primitive is one packet of 9 qw. The tag word (qw 0) is built at 0x10C-0x112 from the batch tag (vf21, copied at 0x10C) with the x word replaced by 0x8004 (0x110):

- x = 0x8004: NLOOP = 4, EOP = 1.
- y, z, w = words 1..3 of the batch tag: PRE = 1, PRIM = TRIANGLE_FAN (5), NREG = 2, REGS = {RGBAQ, XYZF2} (cpu_feed A.5, `mg_shadow.cpp:82-93`).

Following qws: (RGBAQ, XYZF2) for each of the 4 vertices, i.e. `vf25, S0, vf25, S1, vf25, S2, vf25, S3`. There is no A+D inside any output packet. Triangles of one quad: (S0,S1,S2) and (S0,S2,S3). PRMODECONT = 0 and PRMODE = 0x40 (set by the render packet, cpu_feed A.7) means the packet's PRIM shading flags are not used; ABE = 1 comes from PRMODE (inferred).

### 6.2 XGKICK points

| Addr | Source | When | Effect |
|---|---|---|---|
| 0x125 | vi09 = 44 (0x2C) | every triangle that reaches `__OUTPUTS` | sends the ADD packet: ALPHA_1 = 0x68 |
| 0x157 | vi04 = B+9i | each front packet, immediately after its 9 stores | sends a front quad with ADD |
| 0x174 | vi10 = 44 (0x2C) | only if F > 0 (0x16A) | ADD again (no effect on front quads already sent) |
| 0x179 | vi10 = 46 (0x2E) | only if F > 0 and B > 0 | sends SUB packet: ALPHA_1 = 0x62 |
| 0x180 | vi05 = B+90+9j | each back packet (vi03 times) in `__LPoutputs2` | sends a back quad with SUB |
| 0x187 | vi10 = 46 | after the back loop, only if B > 0 | SUB again (no effect) |

Per triangle the order is: ADD state, front quads in group order, ADD (if any front), SUB state, back quads in group order, SUB (if any back). Back quads of a triangle are sent after all of its front quads, and only when at least one front quad exists (6.5).

### 6.3 Double buffering

B = vi13 alternates between 420 and 600 each batch (0x068-0x06C). The output of one batch lives in one buffer. Inside a batch, all triangles reuse the same slots (vi04 = B at 0x114 and vi05 = B+90 at 0x115 per triangle). Packets are therefore read by XGKICK before the slots are reused (open point 11).

### 6.4 Counts

Per triangle: 5 quads maximum (10 triangles). Front/back split is decided per quad. A batch of 42 triangles emits at most 210 quads.

### 6.5 Packets kicked per triangle (from the trace in 2.4)

| Case | Kicks, in order |
|---|---|
| F >= 1, B >= 1 | ADD state (0x125); F front quads (0x157, ADD); ADD state (0x174); SUB state (0x179); B back quads (0x180, SUB); SUB state (0x187) |
| F >= 1, B = 0 | ADD state (0x125); F front quads (0x157); ADD state (0x174); no SUB block (0x175 skips it) |
| F = 0, B = 5 | ADD state (0x125) only. The five back quads are stored at 0x15B-0x163 but never kicked (0x16A skips to 0x188). No SUB state packet. |

Exact condition for the F = 0 case: at 0x16A, vi02 = 0, i.e. every one of the five quads has its z-sign flag set (zsign < 0 strictly; a zero or positive sign counts as front). With F = 0 the triangle contributes nothing to the frame buffer count.

---

## 7. Culling and clipping

### 7.1 Clip-flag rejects (whole triangle)

| Test | Addresses | Rejects |
|---|---|---|
| C-clip | 0x0AD-0x0B4 (clipw on C0,C1,C2; fcand 0x3FFFF) | any C_j with `+x > |w|`, `-x < -|w|`, same for y and z (6 bits per vertex). Rejects if any bit set. |
| T-clip | 0x0B6-0x0BD (clipw on T0,T1,T2; fcand 0x3FFFF) | same on T_j |
| Grazing | 0x0C8-0x0CE (w of vf20 = 1e-4; clipw on x of vf20; fcand 0x3) | \|Q\| <= 1e-4 |

The test uses |w|, not w > 0. A vertex behind the eye with |x|,|y|,|z| <= |w| passes (inferred). No partial clipping: the whole triangle is dropped. There is no guard-band test: sdw does not read VU 0x15/0x16 (3.1). Each fcand is 4 pairs after the last clipw it tests (0x0AF -> 0x0B3, 0x0B8 -> 0x0BC, 0x0C9 -> 0x0CD), as the flag reads require.

### 7.2 Culling of quads

No quad is culled by facing. Both facings are emitted (path A and path B). Quads are classified front/back in screen space (5.4) and sent with ADD or SUB. The only drop of quads is 6.5: when F = 0, the triangle's back quads are not sent. That case is a retail behaviour, confirmed by the trace; whether it is intended is open point 6.

### 7.3 Handedness word at VU 0x1E.x (no effect)

`__main` computes hflag = 32 if det(R0,R1,R2) < 0, else 0 (0x038-0x042), and stores it at 0x042 with `isw.x vi07, 30(vi00)`. The address 30 is qw 0x1E, which the render-info UNPACK had already filled with CS row 1 (`mg_shadow.cpp:331`, VU 0x1E), so the store overwrites only that qw's x word.

- The CPU value of that word is 0.0: CS[1][0] is 0 because `clip_screen_full` is a unit-matrix copy with only [0][0] and [1][1] set (`mg_drawenv.cpp:209-223`).
- The ISW stores the 16-bit integer zero-extended (brief machine fact). The stored word is 0x00000000 (hflag 0) or 0x00000020 (hflag 32).
- Read as a float, 0x00000000 is +0.0. 0x00000020 is a denormal, which the VU flushes to 0.0 when it is used as an operand.
- The output stage reads that word at 0x120 (`lq vf27, 30(vi00)`) and multiplies it by P.y at 0x12B-0x12C. The x result is therefore 0 in both cases.

Effect: CS[1].x is replaced by 0.0, which is exactly the CPU value. The output is the same for mirrored and non-mirrored objects, and for every draw. No behaviour depends on hflag. Native replacements need not reproduce hflag or the store.

---

## 8. Native replacement notes

Must reproduce exactly:
- Â and the light in clip space (5.1, 5.2), including the -0.1 offset and the use of VC rows 0..2 only.
- The facing value Q = cross(E2,E1)·Lc with C_j (xyz, not divided by w), and the |Q| <= 1e-4 reject. The sign of Q chooses path A or B.
- The 20-entry quad lists (5.3). Their winding determines front/back on screen.
- The screen-space front/back test (5.4) and the ADD/SUB assignment.
- Dropping the back quads of a triangle with F = 0 (6.5), if bit-exact.
- Truncation (toward zero) in 12.4 conversion.
- Packet order: ADD prelude, front quads, ADD, SUB prelude, back quads, SUB. ALPHA state changes take effect in packet order.

Not needed:
- hflag and the store at 0x042 (7.3): CS[1].x is 0.0 in every case.

May approximate:
- Float precision of FMAC (the VU truncates toward zero; this matters only at ±1 LSB of 12.4).
- Degenerate quads (zero area) can be emitted or dropped. They have no effect on the count (inferred).
- The COLCLAMP setting from `My_dma_start0` (`cpu_feed D.4`) clamps the per-pixel counter. Reordering ADD and SUB within one pixel only matters at saturation (inferred).

Replacement design: the whole pass is 2-3 triangles per input triangle plus a CPU or shader pass. A shader can compute C, T, Q and the screen-space sign per triangle and emit 10 triangles with ADD or SUB state. Keep the B/SUB ordering per triangle if exact count parity matters.

---

## 9. Open points

1. **TOPS timing (inferred).** CPU order, checked: BASE/OFFSET (60/180) are in the render packet (`mg_shadow.cpp:300-301`). Each batch is an UNPACK with FLG=1 (`mg_shadow.cpp:128`) followed by `prog_vif`, MSCAL 2 (`:129`). The program reads TOPS with `xtop` after that MSCAL and reads the header at vi12+1 (0x067, 0x06D). This is consistent only if the VIF latches the buffer the UNPACK filled into TOPS at MSCAL and then flips TOP (inferred; the brief's machine facts say TOPS flips at each MSCAL/MSCNT). The repo cannot confirm the latch; the render packet's MSCAL 0 also flips it (inferred).
2. **Ordering of the 0x2C/0x2E tags (inferred).** CPU order, checked: the render packet's first DMA chunk ends with MSCAL 0 (`mg_shadow.cpp:339`). The 0x28 UNPACK (NUM 8, covering 0x28-0x2F) is in `SetShadowData` (`mg_shadow.cpp:24`), which is written after that MSCAL, so the stream is MSCAL 0 and then the UNPACK. During MSCAL 0, __main stores vf27 to 0x2C (0x020). The final 0x2C is the GIF tag only if the VIF performs the UNPACK after the VU has passed 0x020. The repo inserts no FLUSH, FLUSHE or MSCNT between them. Whether the VIF stalls a data UNPACK into VU memory while a microprogram runs is not provable here (inferred). If the order inverted, the ADD tag at 0x2C would hold vf27 until the next render packet, and every triangle's first kick (0x125) would send that garbage.
3. **Input w (inferred).** The program uses V_j.w in C and T (0x0A4-0x0AC, 0x097-0x09F). The CPU copies `vertex[index]` as a float4 (`mg_shadow.cpp:121-123`). The w value of MDT positions is assumed to be 1.0 (not checked).
4. **Light sign convention.** Â is the CPU light vector transformed into object space. Whether shadow_light_dir is the travel direction or the direction toward the light is not settled by sdw; only the path-B/A mapping is known (5.3).
5. **Â as a transform.** The program computes A_i = dot(R_i, L) (row i of the object matrix dotted with L), i.e. M3·L (column product). For row-vector matrices the object-space light is L·M^-1. These agree for rotations and for diagonal matrices, but not for general matrices (inferred from 0x009-0x016).
6. **Dropped back quads: intent.** Confirmed by the trace (6.5): a triangle with F = 0 sends no back quads and no SUB state. Every such triangle is one whose five quads all have negative z sign. Whether this is intended is not in the repo (inferred: unintended). It needs a non-affine configuration to occur: see 10.
7. **`__LPoutputs1` (0x16F-0x172).** Counts vi02 down to 0 and advances vi04 by 9 per iteration, with no XGKICK and no stores. Its only effects are vi02 = 0 and a delay before the ADD state re-send at 0x174. Purpose not stated (inferred: a delay).
8. **Batch header word 3.** Written by no CPU line (`mg_shadow.cpp:113-115` leaves word 11 unwritten). The VU reads only words 0 and 1 (0x067, 0x06D). Harmless.
9. **Stale VU reads.** VU 0x17 and 0x18 are never written. __main loads 0x18 into vf25 and stores vf27 (dot of vf25 with Â) to 0x2C at 0x020. That value is overwritten (point 2).
10. **VU 0x39/0x3A.** Read only by __main, and only for the guard qwords. Not written by any shadow CPU path. sdw does not use them.
11. **XGKICK source lifetime (inferred).** Front quads are built in the same slots B..B+44 for every triangle of a batch. Correct only if XGKICK reads its source before the VU writes it again. Hardware behaviour not checked.
12. **Zero-area quads.** Degenerate quads (test 1 Q1, test 2 Q3, test 3 Q1) are emitted with ADD. Their effect on the counter is zero (inferred from the ADD formula).
13. **Z range.** Z_i = trunc(z) is not clamped. Depth values outside [0, 2^24) would wrap (inferred from ftoi4 and the 24-bit field). CS[2][2] and CS[3][2] come from `mg_drawenv.cpp:215-216`.
14. **PRIM TME/IIP.** The batch tag sets TME = 1 and IIP = 1 in its PRIM field. PRMODECONT = 0 uses PRMODE, which turns TME and IIP off (inferred, cpu_feed A.7).
15. **FIX / ALPHA semantics.** The GS ALPHA formula with C = FIX is decoded from the 2-bit fields (3.1). It has not been checked against the GS manual.
16. **Fog byte from truncated w (inferred).** The 0x13x divides give Q = trunc(1/w) and S.w = w·Q. Truncation can leave S.w just below 1.0, so trunc(16·S.w) = 15 and F = 0 instead of 1. F matters only if fog is enabled by PRMODE, which is not checked here.
17. **Buffer overlap for batches of 40-42 triangles (inferred).** With TOPS = 60, a batch of n triangles spans 60..60+3n+1 (`mg_shadow.cpp:98` allows n = 42, i.e. up to qw 187). The other buffer starts at OFFSET = 180 (3.3). For n >= 40 the VU's reads of the last triangles and the VIF's UNPACK into the other buffer touch the same qws 180..187. Safe only if the VIF does not write those qws while the VU still needs them. The repo does not show that (inferred: VIF stall behaviour).
18. **DIV latency for Q reads (inferred).** The mulq reads in 5.4 are 7 pairs after their div. This is correct only if the DIV latency is at most 7 pairs. The latency is not stated in the repo or the brief.

---

## 10. Test vectors

Common inputs unless stated: n = 1 (one triangle). VU memory: TOPS = 60; header {1, 1, 3, 0} at 61; V0..V2 at 62..64. Object matrix (0x08-0x0B) = identity. Light (0x0C.x, 0x0D.x, 0x0E.x) = (3, 0, -4), i.e. L = (3, 0, -4). VC (0x19-0x1C) = identity. SM (0x28-0x2B) = rows (1,0,0,0), (0,1,0,0), (0.75,0,0,0), (0,0,0,1) (projection along L onto z = 0). CS (0x1D-0x20) = rows (1,0,0,0), (0,1,0,0), (0,0,2^22,0), (0,0,0,1). The test omits the CPU's row 3 centre (2048, 2048, z-offset) so the numbers stay small. VU 0x1E.x = 0.0 (7.3). Then:

- Â = (3,0,-4)/5 = (0.6, 0, -0.8). Lc = -0.1·(Â·VC rows 0..2) = (-0.06, 0, 0.08).
- All vertices have w = 1, so P.w = 1 and S = P. Then X = trunc(16·x), Y = trunc(16·y), Z = trunc(16·P.z) >> 4 = trunc(2^22·z).

### Test 1: lit triangle above the plane (path B)

V0 = (0, 0, 0.5, 1), V1 = (0.5, 0, 0.5, 1), V2 = (0, 0.5, 0.5, 1). CCW seen from +z.

- C0 = (0,0,0.5,1), C1 = (0.5,0,0.5,1), C2 = (0,0.5,0.5,1). Clip: all inside (|x|,|y|,|z| <= 1).
- T0 = (0.375,0,0,1), T1 = (0.875,0,0,1), T2 = (0.375,0.5,0,1). Clip: inside.
- E1 = (0.5,0,0), E2 = (0,0.5,0). cross(E2,E1) = (0,0,-0.25), exact (ruling d), so N = (0,0,-0.25).
- Q = N·Lc = (-0.25)(0.08) = -0.02 < 0 -> path B. |Q| > 1e-4, not rejected.
- Screen (X, Y, Z): C0 (0, 0, 2097152), C1 (8, 0, 2097152), C2 (0, 8, 2097152), T0 (6, 0, 0), T1 (14, 0, 0), T2 (6, 8, 0). RGBAQ (1,1,1,64), F = 1, ADC = 0.

| Quad (path B) | S0,S1,S2 | s = E2s.x·E1s.y − E2s.y·E1s.x | Output state |
|---|---|---|---|
| Q1 (C0,C1,T1,T0) | (0,0),(8,0),(14,0) | 0 (degenerate) | ADD (zsign 0 is not negative) |
| Q2 (C1,C2,T2,T1) | (8,0),(0,8),(6,8) | 48 | ADD |
| Q3 (C2,C0,T0,T2) | (0,8),(0,0),(6,0) | −48 | SUB |
| Q4 (T0,T1,T2,T2) | (6,0),(14,0),(6,8) | −64 | SUB |
| Q5 (C2,C1,C0,C0) | (0,8),(8,0),(0,0) | 64 | ADD |

Footprint (far cap): T0, T1, T2 at (6,0), (14,0), (6,8). Output: 3 ADD quads (Q1 degenerate, Q2, Q5), 2 SUB quads (Q3, Q4), each NLOOP 4. Packet order: ADD state, Q1, Q2, Q5, ADD state, SUB state, Q3, Q4, SUB state (F = 3, B = 2, so 6.5 row 1).

### Test 2: back-facing triangle (path A)

V0 = (0, 0, 0.5, 1), V1 = (0, 0.5, 0.5, 1), V2 = (0.5, 0, 0.5, 1). CW seen from +z.

- C0 = (0,0,0.5,1), C1 = (0,0.5,0.5,1), C2 = (0.5,0,0.5,1).
- T0 = (0.375,0,0,1), T1 = (0.375,0.5,0,1), T2 = (0.875,0,0,1).
- E1 = (0,0.5,0), E2 = (0.5,0,0). cross(E2,E1) = (0,0,0.25), so N = (0,0,0.25).
- Q = 0.25·0.08 = +0.02 >= 0 -> path A.
- Screen: C0 (0,0,2097152), C1 (0,8,2097152), C2 (8,0,2097152), T0 (6,0,0), T1 (6,8,0), T2 (14,0,0).

| Quad (path A) | S0,S1,S2 | s | Output state |
|---|---|---|---|
| Q1 (C0,T0,T1,C1) | (0,0),(6,0),(6,8) | −48 | SUB |
| Q2 (C1,T1,T2,C2) | (0,8),(6,8),(14,0) | 48 | ADD |
| Q3 (C2,T2,T0,C0) | (8,0),(14,0),(6,0) | 0 | ADD (degenerate) |
| Q4 (T2,T1,T0,T0) | (14,0),(6,8),(6,0) | −64 | SUB |
| Q5 (C0,C1,C2,C2) | (0,0),(0,8),(8,0) | 64 | ADD |

Far cap: test 2's Q4 order (T2,T1,T0) is at screen (14,0), (6,8), (6,0), a cyclic shift of test 1's (T0,T1,T2) at (6,0), (14,0), (6,8). Same winding, so Q4 is SUB in both. The near cap is ADD in both. Output: F = 3 (Q2, Q3, Q5), B = 2 (Q1, Q4). Packet order: ADD state, Q2, Q3, Q5, ADD state, SUB state, Q1, Q4, SUB state.

### Test 3: mirrored object (det < 0, hflag = 32)

Object matrix (0x08-0x0B) = diag(-1,1,1,1). VC (0x19-0x1C) = the same matrix (object to clip). SM (0x28-0x2B) = rows (-1,0,0,0), (0,1,0,0), (0.75,0,0,0), (0,0,0,1). CS as test 1. __main computes hflag = 32 and stores it into VU 0x1E.x as the integer 0x20, which reads as a denormal and so as 0.0 (7.3). The effective CS row 1 is therefore (0, 1, 0, 0), as in test 1. Same object-space triangle as test 1.

- A = (R0·L, R1·L, R2·L) = (-3, 0, -4); Â = (-0.6, 0, -0.8). det(R0,R1,R2) = −1, so hflag = 32 (stored, but no effect).
- D = -0.1·Â = (0.06, 0, 0.08). Lc = D.x·VC[0] + D.y·VC[1] + D.z·VC[2] = 0.06·(−1,0,0,0) + 0 + 0.08·(0,0,1,0) = (−0.06, 0, 0.08, 0).

- C0 = (0,0,0.5,1), C1 = (-0.5,0,0.5,1), C2 = (0,0.5,0.5,1). Clip: inside.
- T0 = (0.375,0,0,1), T1 = (-0.125,0,0,1), T2 = (0.375,0.5,0,1). Clip: inside.
- E1 = (-0.5,0,0), E2 = (0,0.5,0). cross(E2,E1) = (0,0,0.25), so N = (0,0,0.25). Q = 0.25·0.08 = +0.02 -> path A.
- Screen (X, Y, Z): C0 (0, 0, 2097152), C1 (-8, 0, 2097152), C2 (0, 8, 2097152), T0 (6, 0, 0), T1 (-2, 0, 0), T2 (6, 8, 0). No x shift, since CS[1].x = 0.0.

| Quad (path A) | S0,S1,S2 | s | Output state |
|---|---|---|---|
| Q1 (C0,T0,T1,C1) | (0,0),(6,0),(−2,0) | 0 | ADD (degenerate) |
| Q2 (C1,T1,T2,C2) | (−8,0),(−2,0),(6,8) | −48 | SUB |
| Q3 (C2,T2,T0,C0) | (0,8),(6,8),(6,0) | 48 | ADD |
| Q4 (T2,T1,T0,T0) | (6,8),(−2,0),(6,0) | −64 | SUB |
| Q5 (C0,C1,C2,C2) | (0,0),(−8,0),(0,8) | 64 | ADD |

The mirrored object is classified exactly as the geometry dictates. Output: F = 3 (Q1, Q3, Q5), B = 2 (Q2, Q4). Packet order: ADD state, Q1, Q3, Q5, ADD state, SUB state, Q2, Q4, SUB state. The mirror changes the geometry (C1, T1 at negative x) but adds no offset.

### Why no vector exercises 6.5 (F = 0)

All vectors have w = 1 at every vertex, so the projection is affine. For such a closed prism (inferred), the five quads' signed screen areas sum to zero, so at least one quad is front. The F = 0 case needs a non-affine (perspective) configuration, which is not hand-derived here.

---

## Register inheritance summary

Read from Vu_progmain: vf02.x, vf03.x, vf04.x (Â). Nothing else.

Overwritten by Vu_progmain each draw, read by sdw: VU 0x1E.x (CS row 1 x). It holds 0.0 in every case, so sdw is unaffected (7.3).

Overwritten by Vu_progmain each draw, not read by sdw: VU 0x15, 0x16 (guard), VU 0x2C (dot product, overwritten by the CPU UNPACK, 9.2).

Persist within sdw across batches: vi13 (output toggle).

Nothing else from Vu_progmain is relied on. The sdw program sets every other register it reads.
