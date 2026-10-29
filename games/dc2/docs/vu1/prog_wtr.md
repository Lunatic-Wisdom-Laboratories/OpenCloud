# Vu_prog_wtr (VU1)

Water surface microprogram. The CPU sends a render packet (CreateRenderInfoPacket, `ps2/src/water.cpp:440-585`) that runs Vu_progmain's setup (MSCAL 0), uploads this program to micro 0x64, then sends one strip batch per (row pair, <=27 columns) (MSCAL 2, then MSCNT). Each batch is a triangle strip of 2p vertices. For every vertex the program projects the surface point with the object-to-screen matrix, divides by w, and emits:
RGBAQ = surface colour with alpha scaled by an edge weight and a distance fade; UV = the projected screen pixel, offset by the wave slope scaled by surface_param1 (refraction: the GS samples the previous frame at the displaced pixel); XYZF2 = projected position, fog factor F, and ADC (set when the triangle formed with the two previous vertices fails the guard-band test). The output is one GIF packet per batch, sent with XGKICK from one of two output buffers.

Labels (listing `port/build/vu1/prog_wtr.txt`): `__INIT_DATA__4` 0x064, `__LPpLSl4t1__4` 0x06C, `__NXpLSl4t1__4` 0x06D, `__MAIN_PROG__4` 0x076, `__LPmain__3` 0x091, `__CPwtr` 0x0B8, `__ENDmain__4` 0x0C3. Last pair 0x0CA. Addresses are micro pair units.

Notation: `xw = x/w`, `yw = y/w`, `Q = 1/w` (VU Q after the divide, written `qw` in the listing for the reciprocal lane). VU qw addresses are data-memory qwords. `[..]` = component mask. `far` = qw 0x15.w, `near` = qw 0x16.w (= qw 0x39.w and qw 0x3A.w, section 3.2).

---

## 1. Role and entry points

| Entry | Who issues it | What runs |
|---|---|---|
| MSCAL 0 | render packet, `water.cpp:498` (`cursor[19]`) | Vu_progmain at 0x0000. `bal vi15, __main` (0x0006-0x0054) loads the world/screen matrices, writes guard qwords 0x15/0x16, 0x1E, 0x2C, then `jr vi15` to 0x0002. E is on the jr delay slot, so the program halts with resume PC 0x0002 (inferred from the brief's E rule). Vu_progmain does not touch the water program. |
| MSCAL 2 | first strip of a draw, `water.cpp:832-835` (`prog_vif`) | 0x0002: `iaddiu vi01, vi00, 100; jr vi01` (0x0004). Jumps to 0x064 = `__INIT_DATA__4`. |
| MSCNT | later strips, `water.cpp:836-838` (`progf_vif`) | Resumes at 0x064 (the halt PC left by `nop[E] | b __INIT_DATA` at 0x073). |

Per batch: 0x064-0x072 setup, `bal vi15` to 0x076 (`__MAIN_PROG__4`), vertex loop 0x091-0x0C1, ENDmain 0x0C3-0x0C9 (`xgkick`, restore link, `jr vi15`) returns to 0x073 (`nop[E] | b 0x064`), halt.

Persistence across calls: all VU vf/vi registers persist across halts and across MSCAL 0 / MSCAL 2. VU data memory persists. The water program reads no data written by its own previous batch except the flag registers vi08/vi09 (section 2).

Micro-code is uploaded once per draw by `mgSendVuProg(MG_VU_PROG_USER)` (`water.cpp:606`) into micro 0x064-0x0CA, overwriting the other switchable programs (see `cpu_feed_other.md` D.5).

---

## 2. Register conventions

### 2.1 Integer

| Reg | Value / role | Set by | Notes |
|---|---|---|---|
| vi12 | TOPS: qw address of the current batch input | `xtop vi12` 0x064 | |
| vi13 | output buffer base: 420 (0x1A4) or 600 (0x258) | 0x066-0x06C | toggles each batch; see 2.3 |
| vi14 | stack pointer, 1024 at entry, 1023 while in __MAIN | 0x065, 0x076 (`iaddi -1`), 0x0C6 (`+1`) | |
| vi15 | link (return to 0x073) | `bal` 0x071; `ilwr` 0x0C5 reloads from qw 1023 | |
| vi02 | positions pointer (TOPS+2), +1 per vertex | 0x06F | 0x067 writes counts.y into it; 0x06F overwrites it |
| vi03 | slopes pointer (TOPS+2+2p) | 0x070 | +1 per vertex |
| vi06 | output write pointer | 0x07C (= vi13) | +1 per store |
| vi07 | flag scratch; F_k word of the current vertex (bits 7,6,4) | 0x07D-0x07F, 0x0B0-0x0B1, 0x0BB | |
| vi08 | F_{k-1} at the start of vertex k | 0x033 (progmain: 128); 0x0BB (`vi08 <- vi07`) | 128 at first use of a draw, see 2.3 |
| vi09 | F_{k-2} at the start of vertex k | 0x0B2 source; 0x0BA (`vi09 <- vi08`) | inherited at first vertex, see 2.3 |
| vi10 | 208 (0xD0) = mask of bits 7,6,4 | 0x082 (`iaddiu vi10, vi00, 208`), once per batch in __MAIN | mask for fmand at 0x0B0/0x0B1, compare at 0x0B5 |
| vi11 | remaining vertices, starts at 2p | 0x06E (`iadd vi11, vi01`) | decremented 0x0BE |
| vi01 | scratch: counts.x at 0x06D; window AND at 0x0B2-0x0B3 | | |

### 2.2 Float / Q / ACC

| Reg | Content inside the program | Set by |
|---|---|---|
| vf00 | (0,0,0,1) | hardware |
| vf10, vf11, vf12, vf13 | WS rows 0..3 = clip-space transform (object-to-screen, row-vector form). Read at 0x098-0x09B. | **Inherited from Vu_progmain** (0x014-0x01A, `lqi` from qw 4,5,6,7), see section 9.1 |
| vf15 | GIF tag (copied from TOPS qw 0) | 0x07B |
| vf16 | .w = colour fade: raw `375*Q - 0.25` (0x0AB), clamped to [0,1] (0x0B2, 0x0B5); .xyz scratch | 0x0AB, 0x0B2, 0x0B5 |
| vf17 | after 0x09B: clip (x,y,z,w). After 0x0A7: (xw, yw, zw, Q) | 0x09B, 0x0A6, 0x0A7 |
| vf18 | XYZF2 word: xyz = FTOI4(xw,yw,zw) (0x0B1); .w = fog word (0x0A9, 0x0B0, 0x0B4, 0x0B7, 0x0BA) | 0x0A9, 0x0B1, 0x0BA |
| vf19 | zeroed at 0x076; then `[xyw]` difference scratch (0x0AC/0x0AD). Its sign flags feed the window test. | 0x076 |
| vf20 | position (x,y*P0,z,1), then scratch | 0x091, 0x095 |
| vf21 | slope in, then UV = (xw - Ox + s0*P1, yw - Oy + s1*P1, ...), then FTOI4 of UV | 0x092, 0x096, 0x0AF, 0x0B3, 0x0B9, 0x0BB |
| vf22 | RGBA: colour, then alpha * weight * fade, then FTOI0 | 0x091, 0x097, 0x0B8, 0x0BC |
| vf24 | .x = -0.25 (`addi` with I = -0.25, 0x086), .w = 375.0 (`muli` with I = 375.0, 0x087); .y, .z never written and never read | 0x085-0x087 (pre-loop) |
| vf25 | surface params: .x = surface_param0 (P0), .y = surface_param1 (P1); .z,.w unused | 0x08F |
| vf26 | colour (r,g,b,a) | 0x08E |
| vf27 | screen offset (Ox, Oy, stale, stale) | 0x08D |
| vf28 | screen size (W, H-1, stale, stale) | 0x08C |
| vf29 | fog: .x = fog.offset, .y = fog.near_value, .z = fog.far_value, .w = fog.scale | 0x08B |
| vf30 | guard min: .xyz from qw 0x16; .w = 1/far (0x088; read by 0x089 and 0x0AC) | 0x084, 0x088 |
| vf31 | guard max: .xyz from qw 0x15; .w = far (0x08A, interlocked; stale alternative 1/near in 9.4) | 0x083, 0x08A |
| Q | 1/far (0x087), then 1/vf30.w = far (0x089); then 1/w per vertex (DIV at 0x09F) | 0x087, 0x089, 0x09F |
| ACC | scratch accumulators | |

### 2.3 Inherited state (not set by this program before first use)

| Reg | Read by water at | Value at first use | Effect |
|---|---|---|---|
| vf10-vf13 | 0x098-0x09B | WS rows of the same draw (inherited from Vu_progmain 0x014-0x01A at the MSCAL 0 of that draw) | **Visible**: all positions. Valid for every batch of the draw (not reloaded by MSCAL 2). |
| vi13 | 0x068 | 420 (Vu_progmain sets it at 0x01F on every MSCAL 0) | first batch of each draw writes buffer 600 |
| vi08 | 0x0B2 (first vertex of the first batch of a draw) | 128 (Vu_progmain 0x033) | **not visible**: only the ADC of vertices 0 and 1 of the first batch |
| vi09 | 0x0B2 (first two vertices of the first batch of a draw) | 0 or 128 (Vu_progmain 0x038 `fmand`) | **not visible** as above |
| vi08, vi09 on later batches | | the values left by the previous batch (last two F words) | **not visible**: only ADC of vertices 0 and 1 of a strip. A strip's first kick is at its vertex 2. |

vf24.y, vf24.z, vf19 before 0x076, and vi10 are not inherited (written before reads).

Other Vu_progmain outputs (vf02-vf04, vf19-vf21 in __main, qw 0x1E, qw 0x2C) are not read by this program.

---

## 3. Data memory map

### 3.1 Per-batch input (relative to TOPS)

Written by the VIF `UNPACK V4_32 | FLG` in the strip packet (`water.cpp:816-819`). FLG=1 means TOPS-relative. The strip's DMA CNT size = 4p+2 qw. `n = 2p` = vertex count (2 <= n <= 54).

| TOPS + | Content | Writer | Reader |
|---|---|---|---|
| +0 | GIF tag: EOP=1, PRE=1, PRIM=4 (TRIANGLE_STRIP, bare type), NREG=3, REGS = {1,3,4} = RGBAQ, UV, XYZF2. NLOOP field = 0 (CPU does not set it). | CPU `water.cpp:786-793` | 0x07B copies to vf15; 0x07F-0x080 writes NLOOP=n into x word |
| +1 | counts: x = n, y = n, z = 0, w = 0 | CPU `water.cpp:820-823` | 0x067 (y, discarded), 0x06D (x -> vi01 = n) |
| +2 + j, j = 0..n-1 | positions. j = 2c + r: r=0 -> P(row, c); r=1 -> P(row+1, c). P(i,c) = (min.x + i*dx, min.y, min.z + c*dz, 1.0), dx = (max.x-min.x)/(rows-1), dz = (max.z-min.z)/(columns-1). | CPU `water.cpp:771-775, 800-807` | 0x091 (`lqi vf20, (vi02++)`) |
| +2 + n + j | slopes, paired like positions: j = 2c + r -> slope[row + r][c] (CPU struct: `[0]` = x, `[1]` = y, `[2]` = stale, `[3]` = weight) | CPU `water.cpp:803-805` | 0x092 (`lqi vf21, (vi03++)`) |

TOPS is 60 (BASE 0x3C) or 180 (OFFSET 0xB4) (`water.cpp:461-462`). The batch occupies 2+4p qw (<= 110), so TOPS region = [60,170) or [180,290). Flip order is inferred (VIF double buffer).

Slope values (CPU, `water.cpp:690-733`, from the height grid `h`):
```
slope[r][c][0] = h[r-1][c] - h[r][c]          (0 at r = 0, since previous = current)
slope[r][c][1] = h[r][c] - h[r][c+1]          (c+1 crosses into the next row at c = columns-1)
slope[r][c][2] = never written (stale stack)
slope[r][c][3] = weight, default 1.0, then edges:
    columns: c = 0 and c = columns-1 -> 0.0 ; c = 1 and columns-2 -> 0.6 ; c = 2 and columns-3 -> 0.3   (lines 718-723)
    rows   : r = 0 and rows-1 -> 0.0 ; r = 1 and rows-2 -> 0.6 ; r = 2 and rows-3 -> 0.3   (lines 727-732, overwrite the column values in those rows)
```
`h` is the buffer just written by CWater::Hamon (`water.cpp:282-310`, `height = next`).

### 3.2 Constant / state qwords (VU data memory)

| qw (hex / dec) | Content | Writer | Readers |
|---|---|---|---|
| 0x00-0x02 | zeros | CPU `water.cpp:463-465` | none in water |
| 0x03 | render_word, render_params | CPU 467-470 | none in water |
| 0x04-0x07 | WS = world_screen rows (`mgMulMatrix(world_screen, info->world_screen, matrix)`, `water.cpp:471`) | CPU | Vu_progmain 0x014-0x01A -> vf10-vf13 |
| 0x08-0x0B | world (object) matrix | CPU 472 | Vu_progmain 0x00A-0x00F (into vf15-vf18) |
| 0x0C-0x14 | unk_e0 (9 qw): uninitialised scratchpad, transferred but not assigned | CPU (not assigned) | Vu_progmain 0x00A-0x00D (vf19-vf21) only |
| 0x15 (21) | guard max: before `__main` changes it, (4095, 4095, z, far). After Vu_progmain 0x04E: (4095, 4095, qw57.z, qw57.w) if flags bit 1 clear, else qw57 whole. So .w = far in both cases. | CPU `water.cpp:475,478,480` (xy = 4095; zw = mgRenderInfo guard zw); Vu_progmain stores 0x04E | water 0x083 (vf31) |
| 0x16 (22) | guard min: (1, 1, z, near), or qw58 whole when scissored (same rule, 0x04F). So .w = near. | CPU 476-477, 479; Vu_progmain 0x04F | water 0x084 (vf30) |
| 0x17 (23) | fog coef copy (struct fog) | CPU 481 | **none in water** (the fog used is at 0x3B) |
| 0x18 (24) | screen size (W, H-1, stale, stale) | CPU 482-483 | water 0x08C (vf28) |
| 0x19 (25) | screen offset (Ox, Oy, stale, stale), Ox = mgScreenOffx = 2048 - W/2 | CPU 484-485; `mglib.cpp:445-446, 1335-1336` | water 0x08D (vf27) |
| 0x1A (26) | colour (r, g, b, a) as floats, 0..255 | CPU 486-490 | water 0x08E (vf26) |
| 0x1B (27) | surface params: (surface_param0, surface_param1, colour.b, colour.a) | CPU 491-493 | water 0x08F (vf25) |
| 0x1E (30) | progmain: x = 0 or 32 (sign of its MAC test) | Vu_progmain 0x042 | none in water |
| 0x26 (38) | flags word (bit 1 = scissor, bit 0 = clip, bit 2/3/4/5 = program option/mode, point light, no light) | CPU `water.cpp:500-530` (UNPACK NUM=1 addr 0x26) | Vu_progmain 0x044; **not read by water** |
| 0x2C (44) | progmain temp (vf27 result) | Vu_progmain 0x020 | none in water |
| 0x39 (57) | mgRenderInfo.guard_max (GS coords, (2048+0.55W, 2048+0.55H, 0, far)); .w = far = clip_max[2] (`mg_drawenv.cpp:143-146`, header `mg_drawenv.hpp` "far distance as w") | `mgFlushRenderInfo` `mglib.cpp:1236-1249` (words[11]-[15]), called from frame setup `mglib.cpp:685` | Vu_progmain 0x046 (only when scissored) |
| 0x3A (58) | mgRenderInfo.guard_min (GS coords, (2048-0.55W, 2048-0.55H, 0, near)); .w = near = clip_min[2] (`mg_drawenv.cpp:142`) | same (words[16]-[19]) | Vu_progmain 0x047 (only when scissored) |
| 0x3B (59) | fog: (offset, near_value, far_value, scale) = words[4]-[7] of the same packet, `mg_drawenv.hpp:130-133` | `mgFlushRenderInfo` `mglib.cpp:1236-1243` (once per frame) | water 0x08B (vf29) |

Write order check: mgFlushRenderInfo writes qw 0x39-0x3B, the water render packet writes qw 0x00-0x1B (+ 0x26). No overlap with TOPS regions (60 and up) or output buffers.

### 3.3 Output buffers (program-written)

| Address | Content | Written at |
|---|---|---|
| 420 (0x1A4) and 600 (0x258), chosen by vi13 | GIF packet for one batch: qw 0 = GIF tag; qw 1+3j+k = (RGBAQ, UV, XYZF2) of vertex j, k = 0,1,2 | 0x07C-0x081 (tag), 0x0BF, 0x0C0, 0x0C2 (vertices) |

Size per batch: 1 + 3n qw <= 163 (n <= 54), within each 180-qw slot (420..599 and 600..779). Stack at 1023.

---

## 4. Control flow

### 4.1 Batch flow (per MSCAL/MSCNT)

```
0x064  vi12 <- TOPS
       vi14 <- 1024
       vi01 <- 420
       vi02 <- counts.y (discarded)
       if vi13 == 420: vi13 <- 600 else vi13 <- 420        (0x068 ibeq, 0x06A b, 0x06B / 0x06C delay slots)
       vi01 <- counts.x = n ; vi11 <- n                     (0x06D, 0x06E)
       vi02 <- TOPS+2                                      (positions)
       vi03 <- TOPS+2+n                                    (slopes)
       bal vi15 -> __MAIN (0x076)                          (0x071, delay 0x072)
__MAIN (0x076-0x090):
       vf19 <- 0 ; vi14 <- 1023 ; push vi15 at qw 1023     (0x076-0x077)
       vf15 <- qw(TOPS)  (GIF tag)                          (0x07B)
       vi06 <- vi13                                         (0x07C)
       vi07 <- (n | 0x8000)  ;  qw(vi06).x <- vi07 ; vi06+=1 ; qw.yzw <- vf15.yzw  (0x07D-0x081)
       vi10 <- 208                                          (0x082)
       vf31 <- qw 0x15 ; vf30 <- qw 0x16                    (0x083-0x084)
       vf24.x <- -0.25 ; vf24.w <- 375 (via I)               (0x085-0x087)
       reciprocal chain for vf30.w / vf31.w                 (0x087-0x08A, section 5.2)
       vf26 <- qw 0x1A ; vf25 <- qw 0x1B ; vf27 <- qw 0x19 ; vf28 <- qw 0x18 ; vf29 <- qw 0x3B   (0x08B-0x08F)
       vi07, vi08, vi09 loop state as in 4.2
__LPmain (0x091-0x0C1):  n iterations (vi11 = n down to 0)
       ... per-vertex math (section 5) ...
       vi11 <- vi11 - 1 ; ibne vi11, 0 -> 0x091            (0x0BE, 0x0C1)
__ENDmain (0x0C3-0x0C9):
       xgkick vi13                                         (0x0C3)
       vi15 <- qw 1023 (ilwr) ; vi14 <- 1024              (0x0C5-0x0C6)
       jr vi15  (-> 0x073 nop[E] | b 0x064)                (0x0C9)
```

### 4.2 Branches on flags

| Address | Condition | Target / effect |
|---|---|---|
| 0x068 `ibeq vi13, vi01(420)` | output parity | 0x06C: vi13 <- 600 (taken); else vi13 <- 420 (fallthrough, delay slot 0x06B) |
| 0x0B5 `ibeq vi01, vi10(208)` | vi01 == 0xD0 (W_j == 0xD0, all three vertices pass all tests) | taken -> 0x0B8 (`__CPwtr`), skips 0x0B7 (`addi.w vf18.w += 2048`). Not taken -> ADC set. |
| 0x0C1 `ibne vi11, vi00` | more vertices | 0x091 (loop); else fall through to __ENDmain |
| 0x03A/0x04A (progmain, not water) | progmain's sign and flags tests | see 9.1 |

The window test in 0x0B0-0x0B5 is over three consecutive vertices (section 5.3).

### 4.3 Subroutines and stack

One subroutine: `__MAIN_PROG__4` (0x076), called with `bal vi15` (0x071). Link saved at qw 1023 (0x077), restored at 0x0C5, `jr vi15` at 0x0C9. Stack depth 1.

---

## 5. Per-vertex and per-batch math

### 5.1 Constants (all in the listing)

| Symbol | Value | Where | Meaning |
|---|---|---|---|
| FADE_A | 375.0 | `loi` 0x086 (I) -> `muli.w` 0x087 (vf24.w), read by `maddq.w` 0x0AB | fade numerator |
| FADE_B | -0.25 | `loi` 0x085 -> `addi.x` 0x086 (vf24.x), read by `mulax.w` 0x0AA | fade offset |
| ADC_BIAS | 2048.0 | `loi` 0x0B6 -> `addi.w` 0x0B7 | added to F word = sets bit 15 (ADC) |
| W_MASK | 208 = 0xD0 | vi10, 0x082 | bits 7,6,4 |
| ONE | vf00.w = 1.0 | hardware | clamp limits 1.0 and 0.0 |

I-bit rule (brief) applied to these: 0x086 `addi[I].x` reads I = -0.25 loaded by the previous I pair 0x085; its own `loi 375.0` is read by the non-I `muli.w` at 0x087. 0x0B6 is an I pair whose `loi 2048.0` is read by the non-I `addi.w` at 0x0B7; that pair is the branch delay slot, so I = 2048 holds whether or not 0x0B7 runs.

### 5.2 Per-batch preamble (0x083-0x08F)

```
vf31 = qw 0x15 ;  vf30 = qw 0x16                    // guard max / min (0x083, 0x084)
Q      = 1 / vf31.w        = 1 / far                // div 0x087 (reads vf31.w = far)
vf30.w = 0 + Q             = 1 / far                // addq.w 0x088, waitq: takes the Q of 0x087
Q      = 1 / vf30.w        = far                    // div 0x089 reads vf30.w written by 0x088 (interlocked)
vf31.w = 0 + Q             = far                    // addq.w 0x08A, waitq: takes the Q of 0x089
```
Checked against the listing: 0x0087 `div Q, vf00w, vf31w`; 0x0088 `addq.w vf30, vf19, Q | waitq`; 0x0089 `div Q, vf00w, vf30w`; 0x008A `addq.w vf31, vf19, Q | waitq`. vf19 is zero from 0x076, so the addq results are exactly 1/far and far.

After the preamble: `vf30.w = 1/far`, `vf31.w = far`, with far = qw 0x15.w (= qw 0x39.w, section 3.2). The stale-value alternative (0x089 reads the old vf30.w = near) is in 9.4.

### 5.3 Per vertex j (order j = 0 .. n-1)

Inputs: `P = pos[j] = (px, py, pz, 1)`, `S = slope[j] = (s0, s1, s2, s3)`, `C = colour = (cr, cg, cb, ca)`, `P0`, `P1` (surface_param0/1), WS rows `R0..R3` (qw 4..7), `Ox, Oy` (qw 0x19.xy), `W, H1` (qw 0x18.xy = (screen width, screen height - 1)), fog `(fo, fY, fZ, fs)` = (vf29.x, vf29.y, vf29.z, vf29.w).

```
// 0x095-0x09B  clip position
py'   = py * P0                                                  // mulx.y 0x095
clip  = px * R0 + py' * R1 + pz * R2 + 1.0 * R3                  // (x,y,z,w) component-wise; row-vector form, 0x098-0x09B

// 0x09F-0x0A7  divide
Q     = 1 / clip.w                                               // DIV 0x09F; Q usable from 0x0A6 (7 pairs)
xw    = clip.x * Q ; yw = clip.y * Q ; zw = clip.z * Q           // mulq.xyz 0x0A6
Q     -> vf17.w                                                  // mulq.w 0x0A7: vf17.w = Q (= 1/w)

// 0x097  alpha weight
s3a   = ca * s3                                                  // mul.w 0x097 (vf22.w, vf21.w)

// 0x0A8-0x0A9  fog base (vf18.w)
fv    = fo + fs * Q                                              // mulax.w 0x0A8 (ACC.w = vf29.x), maddq.w 0x0A9 (vf18.w)

// 0x0AA-0x0AB  alpha fade, raw
fade0 = 375.0 * Q - 0.25                                         // mulax.w 0x0AA (ACC.w = vf24.x), maddq.w 0x0AB (vf16.w = vf24.w*Q + ACC.w)

// 0x0AC-0x0AD  window differences (vf19.xyw); flags are sampled 4 pairs later
//   0x0AC : vf19.xyw = vf30 - vf17   (MAC sign flags read by fmand at 0x0B0)
//   0x0AD : vf19.xyw = vf17 - vf31   (MAC sign flags read by fmand at 0x0B1)

// 0x0AE-0x0AF  UV, raw
ux    = xw - Ox + s0 * P1                                        // suba 0x0AE (ACC = vf17 - vf27), maddw 0x0AF (vf21 = vf21 + ACC)
uy    = yw - Oy + s1 * P1

// 0x0B0-0x0B1  window flags into vi07 (MAC of 0x0AC, then MAC of 0x0AD ANDed onto it)
// 0x0B0 fmand vi07 = MAC(0x0AC) & vi10 ; 0x0B1 fmand vi07 = MAC(0x0AD) & vi07
passX = (vf30.x - xw) < 0 AND (xw - vf31.x) < 0      -> bit 7     i.e. vf30.x < xw < vf31.x
passY = (vf30.y - yw) < 0 AND (yw - vf31.y) < 0      -> bit 6     i.e. vf30.y < yw < vf31.y
passW = (vf30.w - Q) < 0  AND (Q - vf31.w) < 0       -> bit 4     i.e. 1/far < 1/w < far
F_j   = (passX ? 0x80 : 0) | (passY ? 0x40 : 0) | (passW ? 0x10 : 0)     // = vi07 after 0x0B1, always a subset of 0xD0
W_j   = F_{j-2} & F_{j-1} & F_j                                  // vi09 & vi08 & vi07 (0x0B2, 0x0B3)
ADC_j = (W_j != 0xD0)                                            // ibeq vi01, vi10 at 0x0B5; equal -> skip 0x0B7

// 0x0B0  fog clamp, upper (vf18.w)
fv    = min(fv, vf29.y)                                          // miniy.w 0x0B0 (fY = vf29.y)
// 0x0B3  UV lower clamp (vf21, all lanes)
ux    = max(ux, 1.0) ; uy = max(uy, 1.0)                         // maxw.xyzw 0x0B3 (vf00.w = 1.0)
// 0x0B2, 0x0B5  alpha fade clamp (vf16.w)
fade  = max(min(fade0, 1.0), 0.0)                                // miniw.w 0x0B2, maxx.w 0x0B5 (vf00.x = 0)
// 0x0B4  fog clamp, lower (vf18.w)
fv    = max(fv, vf29.z)                                          // maxz.w 0x0B4 (fZ = vf29.z)
// 0x0B7  ADC bias (only when ADC_j)
if ADC_j: fv = fv + 2048.0                                       // addi.w 0x0B7 (I = 2048 from 0x0B6)

// 0x0B8  alpha
A     = s3a * fade                                               // mulw.w 0x0B8 (vf22.w)
// 0x0B9  UV upper clamp
ux    = min(ux, W)   ; uy = min(uy, H1)                          // mini.xy 0x0B9 (vf28)

// 0x0BA  XYZF2 word 3:  F word
Wd    = FTOI4(fv)            // ftoi4.w 0x0BA
F     = (Wd >> 4) & 0xFF      // bits 11..4
ADC   = (Wd >> 15) & 1        // bit 15 (= ADC_j when 0 <= fv < 2048, section 9.2)

// 0x0B1  XYZF2 words 0..2
Xw    = FTOI4(xw)            // ftoi4.xyz 0x0B1 ; low 16 bits used
Yw    = FTOI4(yw)
Zw    = FTOI4(zw)            // Z field = bits 27..4 of this word

// 0x0BB  UV
UVx   = FTOI4(ux) ; UVy = FTOI4(uy)      // ftoi4.xyzw 0x0BB (x, y used, z and w ignored)

// 0x0BC  colour
RGBAQ = (FTOI0(cr), FTOI0(cg), FTOI0(cb), FTOI0(A))              // ftoi0.xyzw 0x0BC
```

Notes on this block:
- Every VF value is consumed after it is produced, in program order (section 9.3). Producer -> consumer pairs: vf18.w 0x0A9 -> 0x0B0 -> 0x0B4 -> 0x0B7 (if ADC) -> 0x0BA -> stored 0x0C2; vf16.w 0x0AB -> 0x0B2 -> 0x0B5 -> 0x0B8 -> vf22.w -> 0x0BC -> stored 0x0BF; vf21.xy 0x0AF -> 0x0B3 -> 0x0B9 -> 0x0BB -> stored 0x0C0. The clamp order is the code order, and the fog clamp is min-then-max.
- The window flags are the MAC sign flags of the two subtractions. Sign bit = 1 when the lane result is negative, so the test is a strict "<" on each side. vi10 = 0xD0 (0x082) selects x, y and w. `fmand vi07, vi07` at 0x0B1 ANDs the 0x0AD flags onto the 0x0AC flags, so F_j = bit 7 (x), bit 6 (y), bit 4 (w) of the two-sided test. Only x, y and w are tested: the `sub.xyw` ops at 0x0AC and 0x0AD clear the Z flags (ruling c), and vi10 = 0xD0 masks bit 5 out in any case.
- W_j = 0xD0 iff bits 7, 6 and 4 are set in all three F words. Every F word is a subset of 0xD0 (the fmand mask, and inherited vi08/vi09 are 0 or 128), so no other bit can be set in W_j. Equality with 0xD0 means: vertices j-2, j-1 and j each pass the X, Y and W tests.
- `s2` (slope[2], never written by the CPU) is in `vf21.z` after 0x0AF. The packed UV format uses only x and y, so `s2` has no effect on any output word. Same for `vf21.w` and `vf27.z/w`.
- RGB are not scaled by any light or fog; only alpha has the weight and fade.
- The Z of XYZF2 is not clamped; the xy UV is clamped to [1, W] x [1, H-1]. There is no lower clamp on the XYZ vertex position (only the ADC rule).

### 5.4 Final formulas (sequential reading, as used by the listing)

With Q = 1/w, far = qw 0x15.w, fY = vf29.y, fZ = vf29.z:

```
UV     : ux = min(max(xw - Ox + s0*P1, 1), W) ; uy = min(max(yw - Oy + s1*P1, 1), H-1)
         UV = (FTOI4(ux), FTOI4(uy))
alpha  : A  = FTOI0( ca * s3 * max(min(375*Q - 0.25, 1), 0) )
         RGB = FTOI0(cr, cg, cb) (unscaled)
fog    : fv = max(fZ, min(fY, fo + fs*Q))  ; F = (FTOI4(fv + 2048*ADC_j) >> 4) & 0xFF
         ADC bit (word 3, bit 15) = ADC_j (valid for 0 <= fv < 2048, 9.2)
ADC    : F_k = 0x80*[vf30.x < xw_k < vf31.x] | 0x40*[vf30.y < yw_k < vf31.y] | 0x10*[1/far < 1/w_k < far]
         ADC_j = NOT( F_{j-2} & F_{j-1} & F_j == 0xD0 )   (vi08, vi09 carried between batches)
```

With the stated guard values, `vf30.x`, `vf30.y` are 1 (unscissored) or the scissor guard min; `vf31.x`, `vf31.y` are 4095 or the scissor guard max.

---

## 6. Output

| Item | Value |
|---|---|
| GIF tag word 0 | `x = n | 0x8000` (NLOOP = n vertices, EOP = 1). Written by VU (0x07D-0x080): `vi07` = 0x7FFF + 1 = 0x8000, `ior` with vi11 = n. The iswr stores the 16-bit value zero-extended into the 32-bit field (brief; the word is raw bits, not a float). The CPU's own word 0 is overwritten in x only; EOP is also set by the CPU. |
| GIF tag words 1..3 | copied from TOPS qw 0 (`sqi.yzw vf15`, 0x081): PRE=1, PRIM=4 (TRIANGLE_STRIP), NREG=3, REGS = RGBAQ, UV, XYZF2. |
| per vertex j, 3 qw | RGBAQ (`sqi.xyzw vf22`, 0x0BF); UV (`sqi.xyzw vf21`, 0x0C0); XYZF2 (`sqi.xyzw vf18`, delay slot of 0x0C1, 0x0C2) |
| ADC | bit 15 of XYZF2 word 3 (vf18.w). Only the XYZF2 qw carries it. |
| UV packing | 10.4 integer texel values in x, y of the UV qw (FTOI4 result). PRMODE 0x158 has FST=1, so UV is used in texel units. |
| xgkick | 0x0C3, `xgkick vi13`: sends 1 + 3n qw from the buffer chosen at 0x066-0x06C. |
| Buffer toggle | vi13 flips per batch; vi13 is reset to 420 at every MSCAL 0 (Vu_progmain 0x01F), so the first batch of each draw uses 600. |
| Triangle kick | GS strip: triangle (j-2, j-1, j) is kicked by vertex j unless ADC_j = 1 (ADC on vertex 0 and 1 has no effect since no triangle is kicked there). |

GS state for the strip (CPU): PRMODECONT = 0, PRMODE = 0x158 | (fog ? 0x20 : 0) (IIP, TME, ABE, FST, optional FGE), TEX0 PSMCT24 at the frame copy, TEXA TA0=TA1=0x80, AEM=0 (`water.cpp:546-549, 753-756`). Fog colour FOGCOL from `info->fog_color` rules (`water.cpp:551-568`).

---

## 7. Culling and clipping

Per-vertex (not per-primitive) tests, all done on normalised coordinates after the divide:

| Test | Passes when | Flag bit | Source line |
|---|---|---|---|
| X guard | vf30.x < xw < vf31.x | 7 | 0x0AC, 0x0AD (sampled 0x0B0, 0x0B1) |
| Y guard | vf30.y < yw < vf31.y | 6 | 0x0AC, 0x0AD |
| W window | 1/far < 1/w < far, i.e. vf30.w < Q < vf31.w with Q = 1/w | 4 | 0x0AC, 0x0AD |

Guard values (after Vu_progmain 0x04E-0x04F):
- Scissor bit (0x26 bit 1) clear: vf31.xy = (4095, 4095); vf30.xy = (1, 1). Full GS 12.4 range, the screen is never scissored.
- Scissor set: vf31.xy = mgRenderInfo.guard_max.xy = (2048 + 0.55W, 2048 + 0.55H) and vf30.xy = guard_min.xy = (2048 - 0.55W, 2048 - 0.55H) (`mg_drawenv.cpp:139-144`).
- w: vf31.w = far, vf30.w = 1/far (interlocked, section 5.2). The W window therefore accepts w in (1/far, far) and does not test near. The stale-value alternative that gives near < w < far is in 9.4.

Rejection: a triangle is rejected by setting ADC on its last vertex when any of its three vertices fails any of the three tests (section 5.3). Nothing is clipped or reshaped. Vertices outside the guard keep their computed XYZ (possibly outside the 0..65535 12.4 range; the GS ignores the triangle).

---

## 8. Native replacement notes

Must reproduce exactly:
- The clip transform, the divide, and the three per-vertex flags (section 5.3), with the W window 1/far < 1/w < far (interlocked reading, 9.4). The triangle drop rule is the sliding window over vertices (j-2, j-1, j) with the per-batch carry-over at batch starts (only vertices 0 and 1 of a strip see the previous batch's flags; they cannot kick, so they do not change the image).
- The UV formula including the 1.0 lower clamp and the W, H-1 upper clamp. The offset uses the unscaled slope x and y times surface_param1; the alpha uses the unscaled edge weight s3.
- The fog clamp order: min with vf29.y, then max with vf29.z (5.4, 9.2).
- The 10.4 scaling of UV and XY, and the Z field = trunc(zw) (24-bit).
- F = floor(fv) mod 256 and ADC = bit 15 of 16*(fv + 2048*flag). This needs FTOI4 to return a 32-bit value above 0x7FFF (9.2).
- The fade and alpha: `A = trunc(ca * s3 * clamp(375/w - 0.25, 0, 1))` (truncation toward zero as FTOI0).

May approximate:
- Floating rounding (FMAC truncates; DIV precision). Results are integers after FTOI, so an ULP difference matters only at a truncation boundary.
- Behaviour in the first two vertices of the first batch (vi08/vi09 inherited values): no visible effect.

Equivalent CPU-side constants:
- Screen offset `Ox = mgScreenOffx = 2048 - W/2`, `Oy = 2048 - H/2` (`mglib.cpp:445-446, 1335-1336`).
- XYOFFSET on the GS is 16*Ox, 16*Oy (`mglib.cpp:1339-1340`). Vertex X = FTOI4(xw) is therefore in GS absolute coordinates; the GS subtracts the offset, so the pixel position equals xw - Ox, which is the same as the UV before the slope offset.
- Fog for water is the per-frame fog at qw 0x3B; qw 0x17 (CPU fog copy) is not used by this program.
- far = `mgRenderInfo.guard_max[3]` = `clip_max[2]` (`mg_drawenv.cpp:146`); near = `guard_min[3]` = `clip_min[2]` (`mg_drawenv.cpp:142`).

---

## 9. Open points

### 9.1 Registers inherited from Vu_progmain

- vf10-vf13 (WS rows) are loaded by Vu_progmain __main at 0x014-0x01A on every MSCAL 0. They are the render packet's `world_screen` (qw 4..7). Because MSCAL 2 does not re-run __main, the water program depends on the MSCAL 0 of the same draw. This is a hard dependency of the draw order in `CWater::Draw` (`water.cpp:587-615`).
- vi13 = 420 is set by __main at 0x01F; the first batch of each draw uses 600.
- vi08 (= 128, from 0x033) and vi09 (from the sign test at 0x038) are read for the first vertex window. They have no visible effect (9.2 below). Their value on later batches is the previous batch's F words.
- Vu_progmain's guard stores (0x04E, 0x04F) override the CPU's xy guard values (`water.cpp:477-480`) in both cases. In the unscissored case the result is identical (4095/1). The CPU's zw values are replaced by qw 0x39/0x3A (= mgRenderInfo.guard zw). (Inferred: `info` in `CreateRenderInfoPacket` is mgRenderInfo, so the values are the same and the water's guard w is the same either way.)
- Vu_progmain also writes qw 0x1E (30) and 0x2C (44); water does not read either.
- Vu_progmain's register inputs qw 0x0C-0x14 (unk_e0, uninitialised scratch) reach vf19-vf21 of progmain only. Water zeroes vf19 at 0x076 and overwrites vf20 and vf21 by 0x091-0x092, so the scratch has no effect here.

### 9.2 FTOI4 width and fog range

FTOI4 width. The brief says FTOI is saturating but gives no width. The values that must survive it:
- XY: an accepted vertex has xw, yw in [1, 4095], so 16*xw in [16, 65520]. X is the low 16 bits of this word, so the result must not saturate at 16 bits (a signed 16-bit saturation at 32767 would clip every vertex right of centre, and TV1 X = 0x8000 would be lost).
- ADC: 16*(fv + 2048) for fv in [0, 2048) is in [32768, 65536). Bit 15 of the word is only readable if the result is at least 17 bits wide. A 16-bit saturation would give 0x7FFF and ADC would never be raised.
- Assumed (inferred): FTOI4 saturates at 32-bit. Out-of-guard vertices can produce much larger values (e.g. xw = 4200 gives 67200), but they are never kicked (every triangle containing them is rejected), so their saturated values never reach the GS. Confirm the width on an emulator trace.

Fog range. The clamped fog value is fv = max(fZ, min(fY, fo + fs*Q)) with fY = vf29.y and fZ = vf29.z (5.3). Its range is [fZ, max(fY, fZ)]. The code needs:
- 0 <= fv < 2048 for the ADC bit to equal ADC_j (for fv < 0, 16*fv has bit 15 set and the ADC bit is wrong; for fv >= 2048 bit 15 is set without ADC).
- fv < 256 for F = floor(fv) without wrap.
The CPU values come from `mgSetFogParam` (`mglib.cpp:1185-1191`) via `SetFogParam` (`mg_drawenv.cpp:424-439`): fY = values[2] = depth_min (the mgSetFogParam near_value argument), fZ = values[1] = depth_max (the far_value argument). Call sites `title.cpp:2022`, `sceneevent.cpp:71`, `dng_main.cpp:1222` pass scene values, not checked here. Not verified: whether fZ >= 0 and max(fY, fZ) < 2048 always hold.
- Note (inferred from the code, not confirmed as intended): with fY < fZ the clamp returns the constant fZ for every vertex. `editexception.cpp:497` calls `mgSetFogParam(50, 400, 100, 0, 0, 255, 0)`, which gives fY = 0, fZ = 255, so F = 255 for every vertex of a draw that uses that fog.

### 9.3 Interlocks (no alternative reading kept)

Per ruling (a), VF read-after-write and lq-loaded VF values are interlocked, so all VF dataflow in this listing is read in program order. The three alternative readings from the earlier draft (0x0B2 -> 0x0B5, 0x0B4 -> 0x0B7, 0x0B7 -> 0x0BA, and 0x0BC -> 0x0BF) are removed. Per ruling (b), integer chains (vi07 0x0B0 -> 0x0B1 -> 0x0B3 -> 0x0B5; vi08/vi09 0x0BA-0x0BB) and ACC chains one pair apart (0x0A8 -> 0x0A9, 0x0AE -> 0x0AF) are read in program order. The only stale-value reading kept is the reciprocal chain (9.4).

### 9.4 Guard w reciprocal chain (0x087-0x08A): stale-value alternative

Primary behaviour (interlocked, 5.2): vf30.w = 1/far, vf31.w = far, so the W window is 1/far < 1/w < far. It accepts w in (1/far, far) and does not reject w < near.

Primary behaviour (ruling a): the div at 0x089 reads vf30.w only after the addq at 0x088 has written it, so it reads 1/far. Open point, to confirm with an emulator trace: the stale reading (kept as the exception the rules allow, not as the behaviour). The div at 0x089 would read the old vf30.w = qw 0x16.w = near (loaded by `lq` at 0x084) instead of the addq result from 0x088. Then Q = 1/near, vf31.w = 1/near (0x08A), and the window becomes 1/far < 1/w < 1/near, i.e. near < w < far. This matches the comments on guard_min/guard_max (`mg_drawenv.hpp:367-368`). The two readings differ for w in (1/far, near]. TV4 separates them.

### 9.5 Z field

Z = trunc(zw) in 24 bits (bits 27..4 of FTOI4(zw)), with zw = clip.z / clip.w. The depth scale is set entirely by the CPU's world_screen z row (inferred). Not verified against a depth test. Behind-the-camera points (w < 0) are not excluded by the sequential window of 5.3, since Q < 0 fails 1/far < Q.

### 9.6 Slope edges

The UV offset (s0, s1) is not faded at the edges; only alpha is faded by the weight s3. slope[r][columns-1][1] reads h[r+1][0] (or one past the array for the last row, `water.cpp:707`). The UV offset at the last column therefore uses a wrapped/out-of-array height difference. The last row reads `current[columns]` beyond the array. (Inferred from `water.cpp:705-707`; out-of-bounds read not checked at runtime.)

### 9.7 CPU-side coverage bug (confirmed by arithmetic)

`CWater::CreatePacket` (`water.cpp:777`) steps `column` by 27 but position and slope pointers advance by 26 (step back at 810-814). For N >= 28 columns, the last ceil(N/27)-1 point columns are never emitted. E.g. N = 28: points 0..26 and 26 only, point 27 (and cell 26..27) is missing. Confirmed by arithmetic on the cited lines; not run.

### 9.8 Other

- The inherited vi08 value affects nothing visible but makes the first two vertices' ADC depend on the previous batch. Not a bug in output.
- TOPS flip order (60 first or 180 first) is inferred. The spec does not depend on it, only on TOPS = xtop.
- FMAC rounds toward zero, DIV precision is unspecified (brief). Affects truncation boundaries only.
- Halt PC: irrelevant to water. Each draw's MSCAL 0 is followed by MSCAL 2 (`water.cpp:498` then `832-835`), and each batch's MSCNT resumes at 0x064 from the halt in 0x073, so the MSCAL 0 resume PC (0x0002, inferred) is never used by water.

---

## 10. Test vectors

Common assumptions for all vectors:
- WS = identity rows: R0 = (1,0,0,0), R1 = (0,1,0,0), R2 = (0,0,1,0), R3 = (0,0,0,1). So clip = (px, py*P0, pz, 1).
- W = 640, H = 448 (`mgScreenWidth`, `mgScreenHeight`), so Ox = 1728, Oy = 1824, H1 = 447.
- Guard unscissored: qw 0x15 = (4095, 4095, 0, 1000) (far = 1000), qw 0x16 = (1, 1, 0, 0.8) (near = 0.8). After the preamble of 5.2: vf30.w = 1/1000, vf31.w = 1000 (interlocked).
- Fog: offset = 0, vf29.y = 0, vf29.z = 0, scale = 0, so fv = 0.
- P0 = 1, P1 = 2. Previous two vertices (j-2, j-1) pass (F = 0xD0), so W_j = 0xD0 iff F_j = 0xD0.
- Colour C = (128, 128, 128, 128).

### TV1: flat water, zero slope (interior)

Inputs: pos = (2048, 2048, 0, 1); slope = (0, 0, 0, 1).

```
py' = 2048 * 1 = 2048 ; clip = (2048, 2048, 0, 1) ; Q = 1 ; xw = 2048, yw = 2048, zw = 0, Q = 1
fade = max(min(375 - 0.25, 1), 0) = 1 ; A = 128 * 1 * 1 = 128
passX: 1 < 2048 < 4095 yes ; passY: yes ; passW: 1/1000 < 1 < 1000 yes -> F = 0xD0, ADC = 0
fv = 0 -> Wd = 0 -> F = 0, ADC = 0
ux = 2048 - 1728 + 0 = 320 -> max 1 -> min 640 -> 320 -> UV.x = 5120 (0x1400)
uy = 2048 - 1824 + 0 = 224 -> max 1 -> min 447 -> 224 -> UV.y = 3584 (0x0E00)
Xw = FTOI4(2048) = 32768 (low 16 = 0x8000) ; Yw = 32768 (0x8000) ; Zw = 0
```
Expected words: RGBAQ = (128,128,128,128). UV = (5120, 3584). XYZF2 = (X 0x8000, Y 0x8000, Z 0, F 0, ADC 0).
Check: GS pixel = X - 16*Ox = 32768 - 27648 = 5120 -> 320.0 = UV.x / 16. Pixel (320, 224) is the screen centre. UV equals the projected screen position.

### TV2: non-zero slope (refraction and edge weight)

Inputs: as TV1, but slope = (0.5, -0.25, 99, 0.6).

```
s0' = 0.5 * P1 = 1.0 ; s1' = -0.25 * P1 = -0.5           (P1 = 2)
ux = 320 + 1.0 = 321 -> UV.x = 5136
uy = 224 - 0.5 = 223.5 -> UV.y = 3576
A = trunc(128 * 0.6 * 1.0) = trunc(76.8) = 76
s2 = 99: stored in vf21.z only, not in any output word
XYZF2 unchanged from TV1 (vertex is not moved by the refraction)
```
Expected: RGBAQ = (128,128,128,76). UV = (5136, 3576). XYZF2 = (0x8000, 0x8000, 0, 0, 0).

### TV3: vertex outside the X guard

Inputs: pos = (4200, 2048, 0, 1); slope = (0, 0, 0, 1). Everything else as TV1.

```
xw = 4200 ; passX: 4200 < 4095 fails -> bit 7 = 0 -> F_j = 0x50 ( bits 6 and 4 set, passY and passW both yes )
W_j = F_{j-2} & F_{j-1} & F_j = 0xD0 & 0xD0 & 0x50 = 0x50 != 0xD0 -> ADC_j = 1
fv = 0 + 2048 = 2048 -> Wd = FTOI4(2048) = 32768 = 0x8000 -> F = 0, ADC = 1
Xw = FTOI4(4200) = 67200 -> low 16 bits = 1664 (0x0680) ; Yw = 0x8000 ; Zw = 0
ux = 4200 - 1728 = 2472 -> max 1 -> min 640 -> 640 -> UV.x = 10240 (0x2800) ; UV.y = 3584
A = 128
```
Expected: RGBAQ = (128,128,128,128). UV = (10240, 3584). XYZF2 = (X 0x0680, Y 0x8000, Z 0, F 0, ADC 1). The triangle formed with vertex j is not kicked. Vertex j stays in the strip queue.

### TV4: the W window (interlocked reading), with the stale-value result

Inputs: pos = (2048, 2048, 0, 1), slope = (0, 0, 0, 1), WS as above except R3 = (0, 0, 0, 0.7), so clip = (2048, 2048, 0, 0.7). Guard and fog as in the common assumptions (near = 0.8, far = 1000). Previous two vertices pass (F = 0xD0).

```
Q = 1/0.7 = 1.428571 ; xw = yw = 2048/0.7 = 2925.714 ; zw = 0
passX: 1 < 2925.7 < 4095 yes ; passY: yes
passW (interlocked): 1/1000 < 1.428571 < 1000 yes -> F_j = 0xD0 -> W_j = 0xD0 -> ADC = 0
fv = 0 -> Wd = 0 -> F = 0, ADC = 0
A = trunc(128 * 1 * clamp(375*1.428571 - 0.25, 0, 1)) = trunc(128) = 128
Xw = Yw = FTOI4(2925.714) = trunc(46811.4) = 46811 = 0xB6DB ; Zw = 0
ux = 2925.714 - 1728 = 1197.7 -> min 640 -> UV.x = 10240 ; uy = 1101.7 -> min 447 -> UV.y = 7152
```
Expected (interlocked): RGBAQ = (128,128,128,128). UV = (10240, 7152). XYZF2 = (X 0xB6DB, Y 0xB6DB, Z 0, F 0, ADC 0). The triangle (j-2, j-1, j) is kicked. w = 0.7 < near is accepted, as section 7 says.

Stale-value result (open point 9.4, not the behaviour): the window becomes 1/1000 < Q < 1/0.8 = 1.25, and Q = 1.4286 fails. F_j = 0x50, W_j = 0x50 != 0xD0, so ADC = 1 and the triangle is not kicked. The fog word is 2048 (F = 0, ADC bit set). RGBAQ, UV and X/Y are the same. Only the ADC bit and the kick differ.

---

## Register inheritance summary (for the replacement)

| Register | Inherited from | Needed by native code |
|---|---|---|
| vf10, vf11, vf12, vf13 | Vu_progmain (qw 4..7 of the same draw's render packet) | yes: clip transform |
| vi13 | Vu_progmain 0x01F (= 420 on every draw) | yes: first batch uses 600 |
| vi08, vi09 | Vu_progmain 0x033, 0x038 (first batch) or previous batch | no (first two vertices of a strip only) |
| qw 0x15, 0x16 | written by Vu_progmain 0x04E-0x04F (values from the same draw) | yes: guard values (far = qw 0x15.w, near = qw 0x16.w) |
| qw 0x1E, 0x2C | Vu_progmain | no |
