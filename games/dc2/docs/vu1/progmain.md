# Vu_progmain (VU1)

Shared setup microprogram that every render packet runs before its batches. It is uploaded once by `mgInit` to micro 0x0000-0x0054 and is never replaced by the switchable programs (which load at 0x0064). Its entry 0 (MSCAL 0) runs `__main` (0x0006-0x0054): it loads the per-draw matrices and lighting that the render-info packet placed at VU data 0x03-0x20, builds the light-to-model cross product and the mirror test (vi07 -> data 0x1E.x), selects the guard-band corners (data 0x15/0x16), and stores E27 (vf27 before normalisation) to data 0x2C, then halts. Entry 2 (MSCAL 2) is a dispatcher that jumps to the resident switchable program at 0x0064. Nothing is written to the GIF.

Notation: `QW[a]` = VU data qword a (hex address, qword units). `vfN` / `viN` = VU registers. `.c` = component mask (x, y, z, w). Pairs are addressed in micro pair units, `0xNNNN`. `(inferred)` marks an inference not settled by the listing or the source. Listing: `port/build/vu1/progmain.txt`.

---

## 1. Role and entry points

| Entry | Issued by | What runs |
|---|---|---|
| Upload | `mgInit`, `ps2/src/mglib.cpp:504` (`sceDmaSend(DmaCH1, Vu_progmain)`), once. Not reachable from `mgSendVuProg`: `prog_adr` ids 0-2 (`mglib.cpp:1889-1898`) name only the switchable programs. | MPG NUM 0x55 = 85 pairs to micro 0x0000 (`progmain.txt` header; pairs 0x0000-0x0054). |
| MSCAL 0 | Render-info packets: `mg_visual.cpp:1385` (`write[10]`), `mg_sprite.cpp:145` (`program_call[3]`), `water.cpp:498` (`cursor[19]`), `mg_shadow.cpp:339` (`write[39]`). Immediate 0. | 0x0000 `bal vi15, __main`, then `__main` (0x0006-0x0053), then the E pair 0x0054. Setup only. |
| MSCAL 2 | First batch of a draw: `mg_visual.cpp:1063`, `mg_sprite.cpp:378`, `water.cpp:657`, `mg_shadow.cpp:55`, `visualmotion.cpp:532`. Immediate 2. | 0x0002 `iaddiu vi01, vi00, 100`; 0x0004 `jr vi01` (delay 0x0005); jumps to 0x0064 (`__INIT_DATA` of the resident program). `__main` is not run. |
| MSCNT | Later batches of the same draw: `mg_visual.cpp:1064`, `mg_sprite.cpp:381`, `water.cpp:658`, `visualmotion.cpp:533`. Each follows an MSCAL 2 of the same packet. | Resumes the resident program at its own halt point. Vu_progmain code is not executed. |

Shadow face packets use MSCAL 2 for every batch (`mg_shadow.cpp:55`, `:129`); they never send MSCNT.

Resulting sequence per draw (inferred from the packet builders, section 3 gives the data):

1. Render packet, VIF: UNPACKs of the setup block into data 0x03-0x20 (plus conditional blocks, section 3), the flags UNPACK into 0x26, then MSCAL 0. `__main` runs and halts.
2. The same packet then sends a GS DIRECT block (PRMODECONT, PRMODE, FOGCOL, draw env). This is not VU work.
3. First batch: input UNPACK (FLG=1, relative to TOPS), then MSCAL 2. 0x0002-0x0004 dispatch to 0x0064, the resident program runs its batch and halts at its own `nop[E] | b __INIT_DATA`.
4. Later batches: input UNPACK, then MSCNT, which resumes at that halt point.

**Halt PC after MSCAL 0: irrelevant.** Checked in the CPU builders: every packet that sends MSCAL 0 is followed, before any MSCNT, by a batch whose first VIF program command is MSCAL 2 (visual `mg_visual.cpp:1063`, with `started` local to each `CreateFacePacket` call, `:1096`; sprite `mg_sprite.cpp:378` with `prog_started` reset in `BeginCreatePacket`, `:269`; water `water.cpp:657` with `started` local to `CreatePacket`, `:762`; visualmotion `visualmotion.cpp:532` with `started` local to `CreateFaceMotionPacket`, `:564`; shadow `mg_shadow.cpp:55`, MSCAL 2 on every batch, `:129`). A program upload that sits between them (`mgSendVuProg`, a DMA CALL, `mglib.cpp:1901`, e.g. `mg_sprite.cpp:248`) is not a VIF MSCAL or MSCNT, and MSCAL 2 always enters at 0x0002, so the resume PC after MSCAL 0 is never used.

Persistence: VU vf/vi/Q/I registers and data memory persist across halts and across MSCAL 0 / MSCAL 2. Section 2 lists what the switchable programs can see. Verified consumers: vf10-vf13 (`prog_wtr.md`, `prog_3dsp.md` section 11), QW[21]/QW[22] (section 2.3), QW[30] (section 3.1), vi13 (prog0 line 0x0068). The rest are for the program specs.

---

## 2. Register conventions

State after the VU halts. "Set at" gives the pair that last writes the value. VF results are visible to every later instruction in program order: VF read-after-write hazards are interlocked, so the values below are the sequential-dataflow results. The exception is the MAC flags read by `fmand` at 0x0038, which see the flags of the FMAC op issued four pairs earlier (0x0034), section 4.

### 2.1 Integer registers

| Reg | Value at halt | Set at | Read by a switchable program before it writes it? |
|---|---|---|---|
| vi00 | 0 | hardware | constant |
| vi01 | `flags & 0xFFFF` after an MSCAL 0 run (0x0044 `ilw.x`). The MSCAL 2 entry sets 100 at 0x0002 before `jr vi01`, so the program starts with vi01 = 100. | 0x0044 (MSCAL 0 path); 0x0002 (MSCAL 2 path) | No: the program writes vi01 = 420 at 0x0066 before reading it (prog0 listing). |
| vi02 | 12 | 0x0006 (8), post-increment at 0x0009, 0x000D, 0x000E, 0x000F | Not checked |
| vi03 | 15 | 0x0007 (12), post-increment at 0x000A, 0x000B, 0x000C | Not checked |
| vi04 | 20 | 0x0008 (15), 0x0010 (15), post-increment at 0x0011-0x0013, 0x0018, 0x001C | Not checked |
| vi07 | `flags & 2` (0 or 2) | 0x003C/0x0040 (0 or 32), 0x0045 (2), 0x0048 `iand` | Not checked |
| vi08 | 128 | 0x0033 | Not checked |
| vi09 | 128 if T < 0, else 0 | 0x0038 `fmand vi09, vi08` | Not checked |
| vi13 | 420 | 0x001F `iaddiu vi13, vi00, 420` | **Yes.** The switchable program reads it at 0x0068 (`ibeq vi13, vi01`). 420 makes the first batch of every draw write output buffer 600 (prog0 listing 0x0068-0x006C; `prog_wtr.md` says the same). |
| vi15 | 2 | 0x0000 `bal vi15` (return address = pair after the bal delay slot) | No: the switchable programs start with their own `bal vi15` (prog0 line 32). |
| vi14 | not written | | The program sets it to 1024 at 0x0065 (prog0 listing). |

Only vi01, vi13 and vi14 were checked against the switchable listing; for the others, "Not checked" means the program specs must confirm the first read.

### 2.2 Float registers

| Reg | Value at halt | Set at | Notes |
|---|---|---|---|
| vf00 | (0, 0, 0, 1) | hardware | |
| vf01 | (1, 1, 1, 1) | 0x0006 `sub` gives 0; 0x000A `addw.xyzw vf01, vf01, vf00w` adds 1 | Confirmed. Only vf01.y is read (0x001C). |
| vf02 | R0 normalised: `vf02.c = R0.c * Q_c`, see section 5 | 0x0010 (R0), 0x0022, 0x0025, 0x0028, 0x002B (scales) | Persists. |
| vf03 | R1 normalised | 0x0013 (R1), 0x0023, 0x0026, 0x0029, 0x002C | Persists. |
| vf04 | R2 normalised | 0x0016 (R2), 0x0024, 0x0027, 0x002A, 0x002D | Persists. |
| vf05 | QW[15] = light_color[0] | 0x0011 | Persists. |
| vf06 | QW[16] = light_color[1] | 0x0012 | Persists. |
| vf07 | QW[17] = light_color[2] | 0x0013 | Persists. |
| vf08 | QW[18] = light_color[3] | 0x0018 | Persists. |
| vf09 | QW[19] = ambient (x obj_alpha) | 0x001C | Persists. |
| vf10 | QW[4] = model_screen row 0 | 0x0015 | Persists. Read as data by the other programs (see their specs). |
| vf11 | QW[5] | 0x0016 | Persists. |
| vf12 | QW[6] | 0x0019 | Persists. |
| vf13 | QW[7] | 0x001A | Persists. |
| vf14 | (0, 255, z, w): `.x = 0` (0x000C), `.y = 1 + 254 = 255` (0x001C) | 0x000C, 0x001C | `.z`, `.w` are not written by `__main`; they keep whatever the previous program left (inferred). |
| vf15 | Scissor path: QW[57] (x, y, z, w). Otherwise (4095, 4095, QW[57].z, QW[57].w) | 0x0046 (load), 0x004C-0x004D (only when the scissor bit is clear) | **Overwritten.** Not model_world. |
| vf16 | Scissor path: QW[58] (x, y, z, w). Otherwise (1, 1, QW[58].z, QW[58].w) | 0x0047 (load), 0x004C (only when the scissor bit is clear) | **Overwritten.** |
| vf17 | QW[10] = model_world row 2 | 0x000E | Persists. |
| vf18 | QW[11] = model_world row 3 | 0x000F | Persists. |
| vf19 | QW[12] = light_dir[0] | 0x000A | Persists. |
| vf20 | `.xyz` = QW[8].xyz x QW[9].xyz; `.w` = QW[13].w | 0x002E `opmula` (ACC), 0x002F `opmsub` | Persists. Cross product of the first two model_world vectors, computed before vf15/vf16 are overwritten. |
| vf21 | `.xyz` = vf20.xyz * QW[10].xyz (component-wise); `.w` = QW[14].w | 0x0030 `mul.xyz` | Persists. |
| vf22 | `.x = vf21.y`, `.y = vf21.z`; `.z`, `.w` not written by `__main` (value from before MSCAL 0) | 0x0031 `mr32.xy` | Persists. |
| vf23 | `.x = vf22.y = vf21.z`; `.y`, `.z`, `.w` not written by `__main` (value from before MSCAL 0) | 0x0032 `mr32.x` | Persists. |
| vf24 | `.x = T` = vf21.x + vf21.y + vf21.z = det[QW[8..10].xyz] (0x0033-0x0034); `.y`, `.z`, `.w` not written by `__main` | 0x0033, 0x0034 | Persists. |
| vf25 | QW[24] = eye (mg_visual with program_mode != 0), otherwise whatever QW[24] held (section 3) | 0x0017 | Persists. |
| vf27 | `E27 = vf02_pre * vf25.x + vf03_pre * vf25.y + vf04_pre * vf25.z` (xyzw), `_pre` = before normalisation | 0x001E | Persists. Also stored to QW[44] at 0x0020 (section 2.3). |
| vf31 | `S = vf02_pre^2 + vf03_pre^2 + vf04_pre^2` (component-wise, xyzw) | 0x001A | Persists. |
| Q | `1 / sqrt(S.w)` (the last rsqrt, 0x0029; ready by the 0x002B waitq) | 0x0029 | Persists. The switchable programs set Q before first use (`div` / `rsqrt`), per their specs. |
| I | 254.0 (scissor path) or 4095.0 (other path) | 0x001B (254), 0x004C (4095, only off the scissor path) | Persists. |
| ACC | `.xyz` = (W0.y*W1.z, W0.z*W1.x, W0.x*W1.y) from `opmula ACC, vf15, vf16` (0x002E, ruling d). No later pair writes ACC (0x0030-0x0034 are `mul`, `mr32` and `add`, which do not accumulate), so this persists to halt. `.w` is not set by `opmula.xyz` and is not relied upon. | 0x002E | Persists. |

### 2.3 Data memory written by `__main`

| Qword | Content | Set at |
|---|---|---|
| QW[21] (0x15) | vf15 after 0x004D: scissor path = QW[57] (x, y, z, w); otherwise (4095, 4095, QW[57].z, QW[57].w) | 0x004E `sq` |
| QW[22] (0x16) | vf16 after 0x004C: scissor path = QW[58] (x, y, z, w); otherwise (1, 1, QW[58].z, QW[58].w) | 0x004F `sq` |
| QW[30] (0x1E) `.x` | word 0x00000000 (vi07 = 0) or 0x00000020 (vi07 = 32), zero-extended (section 3.1) | 0x0042 `isw.x` |
| QW[44] (0x2C) | E27 = vf27 as computed at 0x001E (un-normalised) | 0x0020 `sq` |

QW[57] and QW[58] are the CPU's guard corners (0x39, 0x3A), written once per frame (`mglib.cpp:1245-1247`). Their z and w words are copied into QW[21] and QW[22] on both paths, so the stored z and w are the CPU values (section 7).

Readers of these qwords: QW[21]/QW[22] are read by prog0 (`lq` into vf22 and vf15 at 0x038A-0x0401, `prog0.txt`), by prog_3dsp (`lq` into vf31 and vf30 at 0x0088-0x0089 and 0x00DF-0x00E0) and by prog_wtr (`lq` into vf31 and vf30 at 0x0083-0x0084). prog_sdw does not read them. QW[30] is read as in section 3.1. No listing reads QW[44] (the only `44(vi00)` in `port/build/vu1/*.txt` is the `sq` at 0x0020).

---

## 3. Data memory map

Qword addresses (hex). Data from the render-info packet is written by the CPU before MSCAL 0. "stale" means the packet does not assign that qword, so it holds whatever the VU data memory had (VU memory is not cleared between draws, inferred from the absence of any clear). Every row is the same address range in each program's packet, so the columns show the difference per program.

| Qword | mg_visual (`mg_visual.cpp`) | mg_sprite (`mg_sprite.cpp`) | CWater (`water.cpp`) | mg_shadow (`mg_shadow.cpp`) | Read by `__main` |
|---|---|---|---|---|---|
| 0x00-0x02 | stale (UNPACK starts at 0x03) | zero (`unk_20[3]`, `mg_sprite.cpp:111-113`) | zero (`clear[3]`, `water.cpp:463`) | zero (`mg_shadow.cpp:304-306`) | no |
| 0x03 | unk_20, never assigned (`mg_visual.cpp:1277`, `setup->vif[3]` address 0x0003) | unk_50 = (rp3, rp0, rp1, rp2) (`mg_sprite.cpp:114`) | (render_word, rp0, rp1, rp2) (`water.cpp:467-470`) | (rp3, rp0, rp1, rp2) (`mg_shadow.cpp:307`) | no |
| 0x04-0x07 | model_screen (`mg_visual.cpp:1247-1249`) | local_screen (`mg_sprite.cpp:118`) | world_screen (`water.cpp:471`) | world_screen (`mg_shadow.cpp:311`) | yes: vf10-vf13 |
| 0x08-0x0B | model_world (`mg_visual.cpp:1238`) | local_world (`mg_sprite.cpp:119`) | world (`water.cpp:472`) | model matrix (`mg_shadow.cpp:312`) | yes: vf15-vf18 (vf15, vf16 are then overwritten) |
| 0x0C-0x0E | light_dir[0..2] (`mg_visual.cpp:1259`) | stale (`unk_e0[11]`, `mg_sprite.hpp:67`, never assigned) | stale (`unk_e0[9]`, `water.cpp:29`) | .x = `shadow_light_dir[0..2]`, other components 0 (`mg_shadow.cpp:313`, `316-318`) | yes: vf19-vf21 |
| 0x0F-0x12 | light_color[0..3] (`mg_visual.cpp:1262`) | stale | stale | stale | yes: vf05-vf08 |
| 0x13 | ambient x obj_alpha (`mg_visual.cpp:1263-1264`) | stale | stale | stale | yes: vf09 |
| 0x14 | object_color x obj_alpha (`mg_visual.cpp:1275`) | stale | stale | stale | no |
| 0x15 | stale | stale | guard_max with xy = 4095 (`water.cpp:475-480`) | full_max (`mg_shadow.cpp:319`) | written by `__main` (0x004E) |
| 0x16 | stale | stale | guard_min with xy = 1 (`water.cpp:475-480`) | full_min (`mg_shadow.cpp:320`) | written by `__main` (0x004F) |
| 0x17 | stale | fog (`mg_sprite.cpp:123`) | fog.coef (`water.cpp:481`) | stale | no |
| 0x18 | eye (`mg_visual.cpp:1283-1287`) only if program_mode != 0, otherwise stale | unk_00, never assigned (`mg_sprite.hpp:77`) | screen_size (`water.cpp:482`) | stale (`mg_shadow.cpp:322`, write[0] not assigned) | yes: vf25 |
| 0x19 | scissor model_clip row 0 (`mg_visual.cpp:1299-1305`); overwritten by view row 0 when program_mode & 2 (`:1330-1331`) | view_screen row 0, scaled (`mg_sprite.cpp:131-135`) | screen_offset (`water.cpp:484-485`) | view_clip row 0 (`mg_shadow.cpp:326`) | no |
| 0x1A | scissor model_clip row 1 (scissor only); view row 1 (program_mode & 2) | view_screen row 1 | surface colour (`water.cpp:490`) | view_clip row 1 | no |
| 0x1B | scissor row 2 (scissor only); view row 2 | view_screen row 2 | surface_params (`water.cpp:493`) | view_clip row 2 | no |
| 0x1C | scissor row 3 (scissor only); view row 3 | view_screen row 3 | stale | view_clip row 3 | no |
| 0x1D | clip_screen[0] (scissor only, `mg_visual.cpp:1302`) | stale | stale | clip_screen_full[0] (`mg_shadow.cpp:330`) | no |
| 0x1E | clip_screen[1] (scissor only, `mg_visual.cpp:1303`); `.x` = 0.0 | stale | stale | clip_screen_full[1] (`mg_shadow.cpp:331`); `.x` = 0.0 | `.x` written by `__main` (0x0042) every draw; read by the switchable programs (section 3.1) |
| 0x1F | clip_screen[2] (scissor only) | stale | stale | clip_screen_full[2] | no |
| 0x20 | clip_screen[3] (scissor only) | stale | stale | clip_screen_full[3] | no |
| 0x26 | flags, `.x` (`mg_visual.cpp:1378`) | flags, `.x` (`mg_sprite.cpp:179-186`) | flags, `.x` (`water.cpp:529-530`) | **never written.** The shadow render-info packet UNPACKs VU 0x00-0x20 only (count 33 qw, `mg_shadow.cpp:300-333`), and nothing else in the shadow packets writes 0x26. Holds the last value written by a visual, sprite or water packet (section 8) | yes: `.x & 2` (0x0044-0x004A). Also read by prog0 (`ilw.x vi01, 38(vi00)`, `prog0.txt` lines 40, 50, 67, 107, 147, 464, 1496); not read by prog_sdw, prog_3dsp or prog_wtr |
| 0x2C | not written by CPU | not written | not written | not written | written by `__main` (0x0020, E27) |
| 0x2D-0x34 | point lights (`SetPointLight`, `mg_visual.cpp:173-190`, UNPACK 0x2D NUM 8), only when plight_hit and lighting enabled (`mg_visual.cpp:1318-1323`) | no | no | no | no |
| 0x39 | guard_max, once per frame (`mglib.cpp:1245-1246`, `mgFlushRenderInfo`) | | | | yes: vf15 (0x0046) |
| 0x3A | guard_min, once per frame (`mglib.cpp:1247`) | | | | yes: vf16 (0x0047) |
| 0x3B | fog offset, near, far, scale, once per frame (`mglib.cpp:1236-1237`) | | | | no |

Notes:

- Input batch buffers are not in this table: they are placed relative to TOPS (FLG=1 UNPACK). TOPS/BASE/OFFSET are set by the packet builders: BASE 60 (0x3C) and OFFSET 180 (0xB4) for visual, sprite and water (`mg_visual.cpp:341-342`, `mg_sprite.cpp:109`, `water.cpp:461`). Shadow uses `vu1_base` and `vu1_offset` (`mg_shadow.cpp:300-301`). `__main` does not touch them.
- Output buffers used by the switchable programs (420 = 0x1A4, 600 = 0x258) are not written here. `__main` only sets vi13 = 420 (0x001F), see section 2.1.
- Within the visual packet, the scissor clip block (0x19-0x20, 8 qw) and the view block (0x19-0x1C, 4 qw, program_mode & 2) overlap. The view block comes later in the VIF stream (`mg_visual.cpp:1299` then `:1330`), so with both set the view matrix replaces clip rows 0-3 (open point 3).
- The shadow render-info packet writes full_max and full_min to 0x15/0x16 (`mg_shadow.cpp:319-320`). `__main` overwrites both on every draw, and no shadow program reads 0x15/0x16, so those writes have no effect.

### 3.1 QW[30] (0x1E) in detail

**Stored word.** `__main` writes word x of QW[30] at 0x0042 (`isw.x vi07, 30(vi00)`), with vi07 = 0 (not-mirror path, 0x003C) or 32 (mirror path, 0x0040). ISW zero-extends the 16-bit integer into the 32-bit word (orchestrator ruling). So the stored word is exactly 0x00000000 or 0x00000020, and the upper 16 bits of the word are cleared. y, z and w of QW[30] are not written by `__main`.

**Reads of x.**

- Float read (`lq`, or any float use of x): 0x00000000 is +0.0. 0x00000020 is a denormal (32 x 2^-149, about 4.5e-44), which the VU flushes to +0.0. A float read of x therefore gives 0.0 in both cases.
- Integer read (`ilw.x`, low 16 bits): gives 0 or 32.

**CPU value at 0x1E before `__main` runs.** Typical value: 0.0, the clip-matrix element [1][0].

| Program (builder) | What the CPU writes to QW[30] | Typical value | Switchable-program reads of QW[30] |
|---|---|---|---|
| prog0 (visual default program) | Scissor visuals only: `clip_screen[1]` (`mg_visual.cpp:1296-1303`). Non-scissor visuals write nothing, so QW[30] is stale. | x = `clip_screen[1][0]` = 0.0 (`mg_drawenv.cpp:209` unit matrix; [1][0] is not assigned at `:213-219`). yzw = (s, 0, 0) with s = clip_half_h * eye / near_z (`:214`). | `ilw.x vi08, 30` at 0x03CA and 0x040B (`prog0.txt` lines 938, 1007); `ilw.x vi10, 30` at 0x05DC (line 1507); `lq.xyzw vf27, 30` at 0x05A5 (line 1451, float, all four components) |
| prog_sdw (shadow, `group->vu_program = 1`, `mg_shadow.cpp:190`) | `clip_screen_full[1]`, every shadow packet (`mg_shadow.cpp:331`) | x = 0.0; yzw = (full_scale, 0, 0) (`mg_drawenv.cpp:220-223`) | `lq.xyzw vf27, 30` at 0x0120 (`prog_sdw.txt` line 205, float, all four components) |
| prog_3dsp (sprite, `mgSendVuProg(…, 2)`, `mg_sprite.cpp:248`) | Nothing: QW[30] is stale | n/a | None in `prog_3dsp.txt` (no `30(vi00)` read) |
| prog_wtr (water, user program 0, `mainloop.cpp:523`) | Nothing: QW[30] is stale | n/a | None in `prog_wtr.txt` (no `30(vi00)` read) |

Because `__main` overwrites x on every MSCAL 0 before any program runs, the CPU's 0.0 is replaced and never read by a program. A program sees x as integer 0 or 32, or as float 0.0. The yzw components seen by the float reads at prog0 0x05A5 and prog_sdw 0x0120 are the CPU values above, or stale for non-scissor visual draws and for sprite and water draws.

**Consequence for the shadow program.** prog_sdw's only float read of QW[30] (0x0120) sees vf27.x = 0.0 both before and after `__main`. The overwrite therefore does not change its input.

---

## 4. Control flow

Pair addresses, branch targets (computed from the listing immediates), and the delay slot of each branch:

| Pair | Instruction | Target | Delay slot | Effect |
|---|---|---|---|---|
| 0x0000 | `bal vi15, __main` | 0x0006 | 0x0001 (nop) | vi15 = 0x0002; calls `__main` |
| 0x0004 | `jr vi01` | vi01 (= 100 = 0x0064) | 0x0005 (nop) | Dispatch to the resident program (MSCAL 2 path: 0x0002, 0x0003, 0x0004) |
| 0x003A | `ibne vi09, vi00` | 0x003F | 0x003B (nop) | Taken when vi09 != 0, i.e. T < 0 (mirror) |
| 0x003E | `b` | 0x0042 | 0x003F (nop) | Joins the not-mirror path (vi07 = 0, set at 0x003C) to the common tail |
| 0x004A | `ibne vi07, vi00` | 0x004E | 0x004B (nop) | Taken when `flags & 2` != 0 (scissor). Skips the guard constants |
| 0x0053 | `jr vi15` | 0x0002 | 0x0054 (E bit) | Returns from `__main`; E ends the program (section 1) |

Subroutines: `__main` is the only one; its return uses vi15 directly (no stack).

Flag tests:

- Mirror: vi09 = `fmand(MAC, 128)` (0x0038), MAC bit 7 = sign of x of the last FMAC result (vf24.x = T, from 0x0034 `add.x`, four pairs earlier, so the flags are those of the completed op). Result: vi07 = 32 when T < 0, else 0 (0x003C/0x0040).
- Scissor / guard bit: vi07 = `flags.x & 2` where flags = QW[38].x low 16 bits (0x0044-0x0048). Bit value 2 is `MG_3DSPRITE_FLAG_SCISSOR` (`mg_sprite.hpp:39`). The visual writer sets it exactly when it writes the scissor block (`mg_visual.cpp:1296` and `:1341-1342`); water `water.cpp:507`; sprite `mg_sprite.cpp:155`. For shadow draws the flags word is the one left by the last visual, sprite or water packet (section 8).

No loops. No data-dependent loop counts.

---

## 5. Per-draw math, in execution order

There is no per-vertex loop. Inputs: model_world rows W0..W3 = QW[8..11] (vf15..vf18), light directions L0..L2 = QW[12..14] (vf19..vf21), model_screen rows (QW[4..7], vf10..vf13), light colours (QW[15..19], vf05..vf09), eye E = QW[24] (vf25), flags = QW[38].x, guard corners G_max = QW[57] (vf15 at 0x0046), G_min = QW[58] (vf16 at 0x0047).

Only FMAC is used for the products. Each product line below names its pairs.

```
// (1) Light-by-world products, 0x000E-0x0016 (ACC chain, mulax/madday/maddz)
for j in 0..2:                                    // j = 0,1,2 -> vf02, vf03, vf04
    R[j].xyzw = L0.xyzw * Wj.x + L1.xyzw * Wj.y + L2.xyzw * Wj.z
    // Wj = vf(15+j) = QW[8+j]. Components of Wj are scalars; L_k are vectors.
    // R[j] = light-direction matrix (columns L_k) times the xyz of world row j.

// (2) Column sums of squares, 0x0017-0x001A -> vf31 (component-wise over the three rows)
S.xyzw = R[0]^2 + R[1]^2 + R[2]^2                // mula, madda, madd

// (3) Eye transform with the pre-normalised rows, 0x001B-0x001E -> vf27
E27.xyzw = R[0] * E.x + R[1] * E.y + R[2] * E.z  // mulax[I], madday, maddz

// (4) Constant vf14 (0x000C, 0x001B loi 254, 0x001C addi.y)
vf14.x = 0 ; vf14.y = vf01.y + 254.0 = 255.0     // vf01 = (1,1,1,1)

// (5) Column normalisation with Q, 0x0021-0x002D
for c in x, y, z, w:
    Q = 1.0 / sqrt(S.c)                          // rsqrt issued at 0x0021, 0x0023, 0x0026, 0x0029
    for j in 0..2:
        R[j].c = R[j].c * Q
    // mulq.c on vf02, vf03, vf04 in that order (0x0022-0x0024 for x, etc.); each Q serves three pairs

// (6) Cross product and triple product, 0x002E-0x0034
C.xyz = W0.xyz x W1.xyz                          // opmula ACC, vf15, vf16; opmsub vf20, vf16, vf15 (ruling d: fd = a x b exactly, a = W0 = vf15, b = W1 = vf16)
D.xyz = C.xyz * W2.xyz                           // mul.xyz vf21
T     = D.x + D.y + D.z                          // mr32 / add.x chain into vf24.x
// T = det [W0.xyz; W1.xyz; W2.xyz] (vf15, vf16, vf17 are QW[8], QW[9], QW[10])

// (7) Mirror flag, 0x0038-0x0042
vi09 = (MAC.x sign of T) ? 128 : 0               // fmand vi09, vi08 (vi08 = 128)
vi07 = (T < 0) ? 32 : 0                          // branch 0x003A / 0x003C / 0x0040
QW[30].x = vi07 as the word 0x00000000 or 0x00000020   // isw.x, 0x0042 (zero-extended)

// (8) Guard-band select, 0x0044-0x004F
vi01 = QW[38].x & 0xFFFF                         // ilw.x (0x0044)
vi07 = vi01 & 2                                  // (0x0045, 0x0048)
if vi07 != 0:                                    // scissor path, branch 0x004A -> 0x004E
    vf15 = G_max                                 // QW[57] as loaded at 0x0046
    vf16 = G_min                                 // QW[58] as loaded at 0x0047
else:                                            // 0x004C, 0x004D
    I = 4095.0
    vf16.xy = (1.0, 1.0) ;  vf16.zw = G_min.zw   // addw.xy vf16, vf00, vf00w (vf00.w = 1)
    vf15.xy = (0 + I, 0 + I) = (4095, 4095) ; vf15.zw = G_max.zw   // addi.xy vf15, vf00, I
QW[21] = vf15                                    // sq, 0x004E
QW[22] = vf16                                    // sq, 0x004F
QW[44] = E27                                     // sq, 0x0020 (stores E27 before step (5) changes vf02-vf04)
```

Notes:

- The normalisation divides each column c of the 3-row array (R[0].c, R[1].c, R[2].c) by sqrt(S.c). That is column normalisation of the matrix whose rows are the three R vectors, not per-row normalisation.
- The eye transform (3) uses the un-normalised rows: 0x001B-0x001E run before the rsqrt/mulq block starts at 0x0021.
- Q values: Q(x) is used by mulq at 0x0022-0x0024; Q(y) by 0x0025-0x0027; Q(z) by 0x0028-0x002A; Q(w) by 0x002B-0x002D. Each switch point carries `waitq` in the same pair as the first reader. A same-pair `waitq` makes the upper op read the new Q, so the first reader of each Q gets the new value. The reads at 0x0024 and 0x002A of the previous Q require the RSQRT latency to exceed one pair (inferred; the brief gives no figure, but the waitq placement implies it).
- Constants: 254.0 = `loi` 0x437E0000 (0x001B); 4095.0 = `loi` 0x457FF000 (0x004C). The `addw`/`addi` pairs use `vf00`, so vf00.w = 1 gives the 1.0 constants and vf00.x/.y = 0 gives the 0 terms.
- Single-component writes (`.x`, `.xy`, `.y`) leave the other components of the destination unchanged. That is why vf14.z/.w and the z/w components of vf22, vf23 and vf24 are not defined by this program.
- DIV/RSQRT divide by zero (S.c = 0) returns the VU's largest finite value for Q (inferred). Then R.c * Q = 0 when R.c = 0.

---

## 6. Output

No GIF packet, no XGKICK, no DMA output. `__main` produces:

- Registers persisting to the switchable program (section 2.1, 2.2).
- Four data-memory writes: QW[21] and QW[22] (guard corners), QW[30].x (mirror value, word 0x00000000 or 0x00000020), QW[44] (E27, un-normalised).

The guard corner values and the mirror value are consumed by the switchable programs; their use is in their specs (`prog_3dsp.md` section 11 describes the QW[21]/QW[22] reads).

---

## 7. Culling and clipping

`__main` culls nothing and clips nothing. It sets the guard corners that the switchable programs use as clip bounds, and the mirror value they use for orientation. Guard values come from the CPU: QW[57] = guard_max = (2048 + 0.55 W, 2048 + 0.55 H, 0, far), QW[58] = guard_min = (2048 - 0.55 W, 2048 - 0.55 H, 0, near) (`ps2/src/mg_drawenv.cpp:137-146`, GS coordinates with the 2048 offset). Off the scissor path the corners are replaced by the constants (4095, 4095) and (1, 1), keeping the z and w of the CPU values.

---

## 8. Native replacement notes

A native replacement of `Vu_progmain` has to reproduce, exactly:

1. The mirror value: the 32-bit word 0x00000000 or 0x00000020 at data word QW[30].x, where the sign is the sign of T = det of the first three rows of model_world (rows QW[8..10], xyz). T = 0 gives 0 (positive sign). Write the word, not a float 32.0: the VU reads 0x00000020 as a flushed denormal (0.0 as float) and `ilw.x` reads it as 32 (section 3.1).
2. The guard corners at QW[21] and QW[22]: scissor path copies QW[57] and QW[58]; other path is (4095, 4095, z, w) and (1, 1, z, w), with z and w from QW[57] / QW[58].
3. The vi13 = 420 effect: the first batch of each draw writes output buffer 600 (via the switchable program's toggle at 0x0068). A native dispatcher must set this per draw.
4. The entry contract: after MSCAL 0, the switchable programs' inputs in vf10-vf13 (model_screen), vf17-vf19 and vf25 are read by them (their specs list the actual uses).

May be approximated or dropped, as long as the consumers do not read them:

- QW[44] (E27 stored): no listing reads it.
- vf02-vf04, vf20-vf24, vf27, vf31, Q, I, ACC: consumers are the switchable programs, whose specs state which registers they read before writing. `prog_wtr.md` and `prog_3dsp.md` section 11 say they do not read vf02-vf04, vf20-vf21, vf27 or vf31 without writing them first. prog0 has reads of vf02-vf04 and vf31 that the other specs have not checked here.

Cannot be reproduced faithfully (stale input):

- For sprite and water draws, data 0x0C-0x14 is never assigned by the CPU (`mg_sprite.hpp:67`, `water.cpp:29`), so vf19-vf21 and the vf02-vf04 products derived from them are stale scratch memory. A native version must use the same values or define them. Sprite and water consumers do not depend on vf02-vf04 (their specs), so the choice should not show.
- For shadow draws, data 0x0F-0x14 and 0x17-0x18 are stale. Data 0x26 is also stale (the flags word is not written by the shadow packet). `__main` then takes the scissor branch iff bit 1 of the last visual, sprite or water flags word is set (vi07 from 0x0044-0x0048). This changes only QW[21]/QW[22], which no shadow program reads (section 3), so the shadow output does not depend on it. A native version can use any value for the shadow branch.

FMAC/MAC arithmetic: VU floats have no NaN/Inf and the FMAC truncates. In this program the visible results are the guard corners (exact constants and copied CPU values), and the mirror sign (sign of a sum of three products). Truncation of very small T could flip the sign only when T is near zero (inferred, not checked numerically).

---

## 9. Open points

1. **Settled by ruling (b).** Integer results and ACC chains follow program order, so `fmand vi09, vi08` (0x0038) is read by `ibne vi09` (0x003A), `iaddiu vi07` (0x0040) by `isw.x` (0x0042) and `iand vi07` (0x0048) by `ibne vi07` (0x004A) with their listing values. The `mulax`/`madday`/`maddz` chain at 0x000E-0x001E and the `opmula`/`opmsub` pair at 0x002E-0x002F need no latency assumption. The MAC flags are still sampled four pairs after their FMAC op (section 4).

2. **Stale CPU inputs (sprite, water, shadow).** Unassigned fields reach the VU: visual `unk_20` at 0x03 (`mg_visual.cpp:1277`); sprite `unk_e0` at 0x0C-0x16 and `unk_00` at 0x18 (`mg_sprite.hpp:67, 77`); water `unk_e0` at 0x0C-0x14 (`water.cpp:29`); shadow 0x0F-0x14, 0x17-0x18. `__main` reads 0x0C-0x0E and 0x18, so the vf02-vf04, vf25 and vf27 (QW[44]) values of those draws are built from garbage. The switchable programs' specs say whether they use these.

3. **Visual scissor + program_mode & 2 overlap.** The scissor block (0x19-0x20) and the view block (0x19-0x1C) write the same qwords, the view block last (`mg_visual.cpp:1299`, `:1330`). Clip rows 0-3 are lost whenever both flags are set. Confirm this is intended.

4. **vi13 reset every draw.** The batch toggle (prog0 0x0068-0x006C) writes buffer 600 first when vi13 = 420, and `__main` sets 420 on every MSCAL 0 (0x001F). Each draw therefore starts with buffer 600, regardless of which buffer the previous draw's last batch used. Whether the GS/XGKICK ordering allows this (the previous draw's last output may still be in flight) is not checked here.

5. **Degenerate normalisation.** When a column of S is zero (e.g. S.w in the identity test), Q is the VU's largest finite value (inferred). The result is 0 when the numerator is 0 and very large otherwise. Not checked against hardware.

6. **I register.** I is 4095.0 off the scissor path and 254.0 on it. No consumer found in the listings reads I without its own `loi` first (not all listings checked).

### 9.1 Corrections to the earlier reading

- Confirmed: MSCAL 0 runs the setup and halts; MSCAL 2 dispatches to 0x0064; vf01 = (1,1,1,1); vf14.y = 255; vi13 = 420 at 0x001F; the four data-memory writes; the cross-product and mirror logic; the guard branch; the stored qwords; every MSCAL 0 is followed by an MSCAL 2 before any MSCNT (section 1).
- Corrected: vf02-vf04 are not "transforms of rows by vf19-21" as a matrix product on rows. They are R[j] = sum over k of L_k * W_j.k (section 5 step 1), with W_j = vf15-vf17. vf31 is the column-wise sum of squares of R, and vf02-vf04 are normalised per column, not per row.
- Corrected: vf27 is computed from the un-normalised R (0x001E precedes the mulq block at 0x0022), not from the normalised rows.
- Corrected: the triple product uses vf15 and vf16 (QW[8], QW[9]) before they are overwritten at 0x0046, so the cross product is of the first two model_world rows and T is the determinant of the first three rows.
- Corrected: the scissor test is on value 2 (bit 1 of the flags word), not bit 2.
- Corrected: vi01 = 100 set at 0x0002 is not a consumed input: the switchable program overwrites vi01 at 0x0066 before any read. The consumed value is vi13 (0x0068).
- Corrected: QW[21] and QW[22] are written on both paths: off the scissor path they are (4095, 4095, z, w) and (1, 1, z, w), with z and w from QW[57] / QW[58] (section 2.3).
- Corrected: the shadow render packet does not write QW[38] (flags), so the shadow guard branch reads a stale value (section 3, 0x26). Its effect is confined to QW[21]/QW[22], which prog_sdw does not read.
- Corrected: prog0 also reads QW[30] as a float qword (`lq vf27, 30` at 0x05A5, `prog0.txt` line 1451), which the earlier list omitted (section 3.1).
- Corrected: the consumers of QW[21]/QW[22] named earlier as "through vf15-vf16 (prog_3dsp.md section 11)" are wrong for prog_3dsp and prog_wtr. Those programs load QW[21]/[22] into their own vf31/vf30 (section 2.3), and prog0 loads them into vf22/vf15.
- Sibling spec note (moved from the open points, not edited here): `prog_3dsp.md` section 11 calls QW[21] the "vf31 source" and QW[22] the "vf30 source". In this program QW[21]/[22] are stored from vf15/vf16 (0x004E/0x004F); the vf31/vf30 names belong to prog_3dsp's own registers, which load them at 0x0088-0x0089.

---

## 10. Test vectors

All three use the same memory layout: QW[4..7] = identity (not used by the arithmetic), QW[8..11] = world rows, QW[12..14] = light directions L0, L1, L2 (with w = 0), QW[15..19] = arbitrary light colour and ambient, QW[24] = eye E = (2, 3, 5, 1), QW[57] = (2300, 2250, 0, 1000), QW[58] = (1800, 1850, 0, 5). Sequential dataflow (section 2) applies. Expected values are listed for the registers and memory addresses named in section 2.3.

### Vector 1: identity model_world, no scissor flag

Inputs: QW[8] = (1,0,0,0), QW[9] = (0,1,0,0), QW[10] = (0,0,1,0), QW[11] = (0,0,0,1). L0 = (1,0,0,0), L1 = (0,1,0,0), L2 = (0,0,1,0). QW[38].x = 0x00000000.

- R0 = L0*1 + L1*0 + L2*0 = (1,0,0,0). R1 = L0*0 + L1*1 + L2*0 = (0,1,0,0). R2 = L0*0 + L1*0 + L2*1 = (0,0,1,0).
- S = (1,1,1,0). Q(x) = Q(y) = Q(z) = 1.0; Q(w) = 1/sqrt(0) -> largest finite value (inferred), times R.w = 0 gives 0.
- vf02 = (1,0,0,0), vf03 = (0,1,0,0), vf04 = (0,0,1,0) after normalisation (each column multiplied by 1).
- E27 = R0*2 + R1*3 + R2*5 = (2,3,5,0). vf27 = (2,3,5,0). QW[44] = (2,3,5,0).
- C = W0 x W1 = (1,0,0) x (0,1,0) = (0,0,1). D = C * W2 = (0,0,1) * (0,0,1) = (0,0,1). T = 0 + 0 + 1 = 1 > 0.
- vf20 = (0,0,1, QW[13].w). vf21 = (0,0,1, QW[14].w). vf22.xy = (vf21.y, vf21.z) = (0,1). vf23.x = vf22.y = 1. vf24.x = (0 + 0) + 1 = 1 = T.
- vi09 = 0, vi07 = 0, QW[30].x word = 0x00000000 (integer 0, float 0.0).
- vi07 = flags & 2 = 0: non-scissor path. I = 4095.0. vf15 = (4095, 4095, 0, 1000); vf16 = (1, 1, 0, 5).
- QW[21] = (4095, 4095, 0, 1000). QW[22] = (1, 1, 0, 5).
- vf14 = (0, 255, z, w).

### Vector 2: mirrored model_world, no scissor flag

Inputs as vector 1 except QW[10] = (0,0,-1,0) (W2 = (0,0,-1,0)), i.e. mirror in z.

- R0 = (1,0,0,0), R1 = (0,1,0,0), R2 = L0*0 + L1*0 + L2*(-1) = (0,0,-1,0).
- S = (1,1,1,0) (squares). Normalised: vf04 = (0,0,-1,0) (column factors 1).
- E27 = R0*2 + R1*3 + R2*5 = (2, 3, -5, 0). vf27 = (2,3,-5,0). QW[44] = (2,3,-5,0).
- C = (0,0,1) (W0 and W1 unchanged). D = C * W2 = (0,0,1)*(0,0,-1) = (0,0,-1). T = -1 < 0.
- vf20 = (0,0,1, QW[13].w). vf21 = (0,0,-1, QW[14].w). vf22.xy = (0,-1). vf23.x = vf22.y = -1. vf24.x = (0 + 0) + (-1) = -1 = T.
- vi09 = 128, vi07 = 32, QW[30].x word = 0x00000020 (integer 32; float read 0.0 after flush).
- Non-scissor path as vector 1: QW[21] = (4095, 4095, 0, 1000), QW[22] = (1, 1, 0, 5).

### Vector 3: identity model_world, scissor flag set

Inputs as vector 1 except QW[38].x = 0x00000002.

- Same R, S, vf27, C, D, T, vf20-vf24 and QW[44] as vector 1: T = 1, vi07 (mirror) = 0, QW[30].x word = 0x00000000.
- vi07 = 2 & 2 = 2 != 0: scissor path. No `loi 4095`; I = 254.0.
- QW[21] = G_max = (2300, 2250, 0, 1000) (loaded at 0x0046). QW[22] = G_min = (1800, 1850, 0, 5) (loaded at 0x0047).
- Other flag bits (e.g. 0x00000004, program_option) do not change the result: only bit value 2 is tested here.

---

Byline: Read-only analysis of `port/build/vu1/progmain.txt`, CPU source `ps2/src` (mglib.cpp, mg_visual.cpp, mg_sprite.cpp, water.cpp, mg_shadow.cpp, mg_drawenv.cpp, visualmotion.cpp, mainloop.cpp) and the sibling listings. Not checked against hardware.
