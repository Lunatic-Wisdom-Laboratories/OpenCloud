#include "common.h"
#include "mw_runtime.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

#include "automap.hpp"
#include "cameracontrol.hpp"
#include "dataread.hpp"
#include "dng_debug.hpp"
#include "dng_effect.hpp"
#include "dng_main.hpp"
#include "dng_status.hpp"
#include "dngfloor.hpp"
#include "dngmenu.hpp"
#include "effscript.hpp"
#include "event.hpp"
#include "event_func.hpp"
#include "font.hpp"
#include "mainloop.hpp"
#include "maintex.hpp"
#include "mapload.hpp"
#include "menucommon.hpp"
#include "menumain.hpp"
#include "mg_drawenv.hpp"
#include "mg_drawprim.hpp"
#include "mg_math.hpp"
#include "mg_memory.hpp"
#include "mg_texture.hpp"
#include "mglib.hpp"
#include "monster.hpp"
#include "quest.hpp"
#include "savedata.hpp"
#include "savedatadungeon.hpp"
#include "sceneevent.hpp"
#include "scenesnd.hpp"
#include "scriptinterpreter.hpp"
#include "snd_seseq.hpp"
#include "userdata.hpp"
#include "water.hpp"

/**
 *
 * Room option entries used by the dungeon floor script.
 *
 */
struct RoomOptions {
    MENU_SPI_ANALYZE_STRUCT1 entries[5]; /**< Room option entries. */
};

/**
 *
 * Dungeon floor manager receiving the current script.
 *
 */
static CDngFloorManager *tree_dngmap;

/**
 *
 * Next dungeon grid cell receiving script information.
 *
 */
static GLID_INFO *tree_glid_info;

/**
 *
 * Memory arena used by the dungeon map script.
 *
 */
static mgCMemory *tree_spi_stack;

/**
 *
 * Passage record receiving the current script properties.
 *
 */
static DNGMAP_ROOT_INFO *tree_spi_rootinfo;

/**
 *
 * Room record receiving the current script properties.
 *
 */
static DNGMAP_ROOM_INFO *tree_spi_roominfo;

/**
 *
 * Number of grid cells read from the current dungeon map script.
 *
 */
static s16 menu_dng_debug_glidcnt;

static int _TREE_MAPINFO(SPI_STACK *stack, int argument_count);
static int _GLID_INFO(SPI_STACK *stack, int argument_count);
static int _ROOT_INFO(SPI_STACK *stack, int argument_count);
static int _ROOM_INFO(SPI_STACK *stack, int argument_count);
static int _ROOM_LINK(SPI_STACK *stack, int argument_count);
static int _ROOM_OPTION(SPI_STACK *stack, int argument_count);
static int _ROOM_TEXNO(SPI_STACK *stack, int argument_count);
static int _ROOM_KEYROOM(SPI_STACK *stack, int argument_count);
static int _ROOM_FLOOR_INFO(SPI_STACK *stack, int argument_count);
static int _ROOM_FLOOR_INFO2(SPI_STACK *stack, int argument_count);
static int _ROOM_TITLE(SPI_STACK *stack, int argument_count);

/**
 *
 * Dungeon grid and floor-property script tags.
 *
 */
static SPI_TAG_PARAM tree_map_tag[] = {
    {"TREE_INFO",  _TREE_MAPINFO    },
    {"GI",         _GLID_INFO       },
    {"RT",         _ROOT_INFO       },
    {"RI",         _ROOM_INFO       },
    {"RI_LINK",    _ROOM_LINK       },
    {"RI_OP",      _ROOM_OPTION     },
    {"RI_TEX",     _ROOM_TEXNO      },
    {"RI_KEYROOM", _ROOM_KEYROOM    },
    {"RF_INFO",    _ROOM_FLOOR_INFO },
    {"RF_INFO2",   _ROOM_FLOOR_INFO2},
    {"RI_TITLE",   _ROOM_TITLE      },
    {NULL,         NULL             },
};

/**
 *
 * Base texture indices of the randomized dungeon room groups.
 *
 */
static int offsetTable_911[] = {0, 4, 8, 12, 16};

void CDngFloorManager::Initialize() {
    dng_no = 0;
    glid_info = NULL;
    glid_num = 0;
    glid_w = 0;
    glid_h = 0;
}

/**
 *
 * Sets dungeon map dimensions and allocates its grid information.
 *
 */
static int _TREE_MAPINFO(SPI_STACK *stack, int argc) {
    int width = spiGetStackInt(stack++);
    int height = spiGetStackInt(stack++);
    int room_count = spiGetStackInt(stack);
    u32 bytes = room_count * sizeof(GLID_INFO);
    tree_dngmap->glid_w = width;
    tree_dngmap->glid_h = height;
    u32 blocks;

    if ((bytes & 0xF) != 0) {
        blocks = (bytes >> 4) + 1;
    } else {
        blocks = bytes >> 4;
    }

    tree_dngmap->glid_info = new (tree_spi_stack->Alloc(blocks + 2)) GLID_INFO[room_count];
    tree_dngmap->glid_num = room_count;
    tree_glid_info = tree_dngmap->glid_info;
    return 1;
}

/**
 *
 * Reads a dungeon map grid cell and initializes its room information.
 *
 */
static int _GLID_INFO(SPI_STACK *stack, int argc) {
    tree_glid_info->type = spiGetStackInt(stack++);
    tree_glid_info->x = spiGetStackInt(stack++);
    tree_glid_info->y = spiGetStackInt(stack++);
    tree_glid_info->unk_6 = spiGetStackInt(stack++);
    tree_glid_info->unk_8 = spiGetStackInt(stack);
    tree_glid_info->link_glid[GLID_DIR_UP] = NULL;
    tree_glid_info->link_glid[GLID_DIR_DOWN] = NULL;
    tree_glid_info->link_glid[GLID_DIR_LEFT] = NULL;
    tree_glid_info->link_glid[GLID_DIR_RIGHT] = NULL;
    tree_glid_info->blink = false;
    DNGMAP_ROOM_INFO *info = &tree_glid_info->room;
    tree_spi_rootinfo = &tree_glid_info->root;
    tree_spi_roominfo = info;
    memset(info, 0, sizeof(*info));
    memset(tree_spi_rootinfo, 0, sizeof(*tree_spi_rootinfo));
    tree_glid_info += 1;
    menu_dng_debug_glidcnt += 1;
    return 1;
}

/**
 *
 * Sets the type, shape, and marker of a dungeon map root.
 *
 */
static int _ROOT_INFO(SPI_STACK *stack, int argc) {
    tree_spi_rootinfo->type = spiGetStackInt(stack++);
    tree_spi_rootinfo->shape = spiGetStackInt(stack++);
    tree_spi_rootinfo->show_mark = spiGetStackInt(stack);
    return 1;
}

/**
 *
 * Initializes the current dungeon room from script data.
 *
 */
static int _ROOM_INFO(SPI_STACK *stack, int argc) {
    tree_spi_roominfo->floor_id = spiGetStackInt(stack++);
    tree_spi_roominfo->order = spiGetStackInt(stack++);
    spiGetStackInt(stack++);
    tree_spi_roominfo->unk_0 = NULL;

    if (argc >= 4) {
        tree_spi_roominfo->unk_0 = mgCopyString(spiGetStackString(stack), tree_spi_stack);
    }

    tree_spi_roominfo->selectable = false;
    tree_spi_roominfo->visited = false;
    tree_spi_roominfo->flag = DNGMAP_ROOM_FLAG_ROOM;
    tree_spi_roominfo->offset_y = 0;
    tree_spi_roominfo->offset_x = 0;
    tree_spi_roominfo->practice_type = -1;
    tree_spi_roominfo->mark_phase = 0;
    tree_spi_roominfo->unk_4c = 0;
    return 1;
}

/**
 *
 * Sets the linked rooms of the current dungeon room.
 *
 */
static int _ROOM_LINK(SPI_STACK *stack, int count) {
    for (int i = 0; i < count; i++) {
        tree_spi_roominfo->link[i] = spiGetStackInt(stack++);
    }

    return 1;
}

/**
 *
 * Applies named option flags to the current dungeon room.
 *
 */
static int _ROOM_OPTION(SPI_STACK *stack, int argc) {
    RoomOptions options = {
        {
         {"start", DNGMAP_ROOM_FLAG_START},
         {"exit", DNGMAP_ROOM_FLAG_EXIT},
         {"boss", DNGMAP_ROOM_FLAG_BOSS},
         {"sub", DNGMAP_ROOM_FLAG_SUB},
         {NULL, 0},
         }
    };
    int flags = DNGMAP_ROOM_FLAG_ROOM;

    for (int i = 0; i < argc; i++) {
        flags |= menu_spi_analyze_func_strcut1(options.entries, spiGetStackString(stack++));
    }

    tree_spi_roominfo->flag |= flags;

    if ((tree_spi_roominfo->flag & DNGMAP_ROOM_FLAG_SUB) || (tree_spi_roominfo->flag & DNGMAP_ROOM_FLAG_BOSS)) {
        tree_spi_roominfo->offset_x = 0;
        tree_spi_roominfo->offset_y = -0x1A;

        if (argc > 1) {
            tree_spi_roominfo->offset_x = spiGetStackInt(stack++);
            tree_spi_roominfo->offset_y = spiGetStackInt(stack);
        }
    }

    return 1;
}

/**
 *
 * Sets the key room references of the current dungeon room.
 *
 */
static int _ROOM_KEYROOM(SPI_STACK *stack, int argc) {
    tree_spi_roominfo->key_room[0] = spiGetStackInt(stack++);
    tree_spi_roominfo->key_room[1] = spiGetStackInt(stack++);
    tree_spi_roominfo->key_room[2] = spiGetStackInt(stack++);
    tree_spi_roominfo->key_room[3] = spiGetStackInt(stack);
    return 1;
}

/**
 *
 * Chooses the texture number of the current dungeon room.
 *
 */
static int _ROOM_TEXNO(SPI_STACK *stack, int argc) {
    int texture_no = spiGetStackInt(stack);

    if (texture_no < 0) {
        int texture_group = abs(texture_no);
        texture_no = GetRandI(4);
        texture_no += offsetTable_911[texture_group - 1];
    }

    tree_spi_roominfo->tex_no = texture_no;
    return 1;
}

/**
 *
 * Sets gameplay options for a dungeon floor.
 *
 */
static int _ROOM_FLOOR_INFO(SPI_STACK *stack, int argc) {
    DNGMAP_ROOM_INFO *info = tree_dngmap->GetDngMapFloorInfo(spiGetStackInt(stack++));

    if (info == NULL) {
        return 0;
    }

    info->geostone = spiGetStackInt(stack++);
    info->fast_destroy_time = spiGetStackInt(stack++);
    info->fishing = spiGetStackInt(stack++);
    info->fishing_record = spiGetStackInt(stack++);
    info->seal = spiGetStackInt(stack++);
    info->spheda = spiGetStackInt(stack++);

    for (int i = 0; i < DNGMAP_SPHEDA_PRIZE_NUM; i++) {
        info->spheda_prize_item[i] = spiGetStackInt(stack++);
        info->spheda_prize_num[i] = spiGetStackInt(stack++);
    }

    return 1;
}

/**
 *
 * Sets practice options for a dungeon floor.
 *
 */
static int _ROOM_FLOOR_INFO2(SPI_STACK *stack, int argc) {
    DNGMAP_ROOM_INFO *info = tree_dngmap->GetDngMapFloorInfo(spiGetStackInt(stack++));

    if (info == NULL) {
        return 0;
    }

    info->practice_type = spiGetStackInt(stack++);
    info->practice_param = spiGetStackInt(stack);
    return 1;
}

/**
 *
 * Sets the display title of a dungeon floor.
 *
 */
static int _ROOM_TITLE(SPI_STACK *stack, int argc) {
    DNGMAP_ROOM_INFO *info = tree_dngmap->GetDngMapFloorInfo(spiGetStackInt(stack++));

    if (info == NULL) {
        return 0;
    }

    char *name = spiGetStackString(stack);

    char empty[4] = "err";

    if (name == NULL) {
        name = empty;
    }

    info->title = mgCopyString(name, tree_spi_stack);
    return 1;
}

void CDngFloorManager::AnalyzeFile(char *data, int size, mgCMemory *memory) {
    tree_spi_stack = memory;
    tree_dngmap = this;
    memory->Align64();
    menu_dng_debug_glidcnt = 0;
    CScriptInterpreter interpreter;
    interpreter.SetTag(tree_map_tag);
    interpreter.SetScript(data, size);
    interpreter.Run();
    tree_spi_stack->Align64();
    RelationGlid();
}

void CDngFloorManager::LoadDataTable(int dungeon, mgCMemory *memory) {
    char path[0x60];
    u8   scratch[0xA000];
    char menu_path[0x40];
    int  size;

    if (memory == NULL) {
        return;
    }

    if (dungeon < 0 || dungeon >= DNGMAP_DUNGEON_NUM) {
        dungeon = 0;
    }

    Initialize();
    dng_no = dungeon;
    sprintf(path, "menu/dngmap/dmap%d.cfg", dungeon);
    u8 *data = (u8 *) MenuCalcBufAlignment((u_long128 *) scratch);
    LoadFile2(path, data, &size, 0);

    if (size > 0) {
        AnalyzeFile((char *) data, size, memory);
    }

    sprintf(path, "menu/dngmap/dflr%d.cfg", dungeon);
    data = (u8 *) MenuCalcBufAlignment((u_long128 *) scratch);
    LoadFile2(path, data, &size, 0);

    if (size > 0) {
        AnalyzeFile((char *) data, size, memory);
    }

    sprintf(menu_path, "flrtitle%d.txt", dungeon);
    size = LoadFileMenu(menu_path, (u_long128 *) data, 1);

    if (size > 0) {
        AnalyzeFile((char *) data, size, memory);
    }
}

GLID_INFO *CDngFloorManager::GetDngMapFloorGlidInfo(int floor_id) {
    int i;

    if (this->glid_info == NULL || (i = 0, this->glid_num) <= 0) {
        return NULL;
    }

    for (; i < this->glid_num; i++) {
        if (this->glid_info[i].type == GLID_TYPE_ROOM && floor_id == this->glid_info[i].room.floor_id) {
            return &this->glid_info[i];
        }
    }

    return NULL;
}

int CDngFloorManager::IsGeoStone(int floor_id) {
    DNGMAP_ROOM_INFO *info = GetDngMapFloorInfo(floor_id);

    if (info != NULL) {
        return info->geostone;
    }

    return 0;
}

int CDngFloorManager::GetSphedaPrize(int floor_id, int index, int *prize, int *count) {
    DNGMAP_ROOM_INFO *info = GetDngMapFloorInfo(floor_id);

    if (info == NULL) {
        return false;
    }

    if (index < 0) {
        return false;
    }

    if (index >= DNGMAP_SPHEDA_PRIZE_NUM) {
        index = DNGMAP_SPHEDA_PRIZE_NUM - 1;
    }

    if (prize != NULL) {
        *prize = info->spheda_prize_item[index];
    }

    if (count != NULL) {
        *count = info->spheda_prize_num[index];
    }

    return true;
}

int CDngFloorManager::GetSphedaPrize(int index, int *prize, int *count) {
    CSaveDataDungeon *dungeon = menu_GetSaveDataDungeon();

    if (dungeon == NULL) {
        return false;
    }

    return GetSphedaPrize(dungeon->floor_id[dungeon->stage_id], index, prize, count);
}

int CDngFloorManager::IsPlaySubGame() {
    int               games = 0;
    DNGMAP_ROOM_INFO *info = GetActiveFloorInfo();

    if (info == NULL) {
        return 0;
    }

    if (info->fishing != 0) {
        games |= DNGMAP_SUB_GAME_FISHING;
    }

    if (info->spheda != 0) {
        games |= DNGMAP_SUB_GAME_SPHEDA;
    }

    return games;
}

int CDngFloorManager::IsSealFloor(int floor_id) {
    CSaveDataDungeon *dungeon = menu_GetSaveDataDungeon();

    if (dungeon == NULL) {
        return DNGMAP_SEAL_NONE;
    }

    if (floor_id < 0) {
        floor_id = dungeon->floor_id[dungeon->stage_id];
    }

    DNGMAP_ROOM_INFO *info = GetDngMapFloorInfo(floor_id);

    if (info == NULL) {
        return DNGMAP_SEAL_NONE;
    }

    DNG_FLOOR_SAVE *saved = dungeon->GetFloorInfoPtr(dungeon->stage_id, floor_id);
    int             seal = info->seal;

    if (saved != NULL && (saved->flag & DNG_FLOOR_FLAG_SEAL_CLEAR)) {
        seal = DNGMAP_SEAL_NONE;
    }

    CUserDataManager *user = GetUserDataMan();

    if (user != NULL) {
        int members = user->GetNowPartyMember();

        if (seal == DNGMAP_SEAL_MONICA && !(members & (1 << USER_CHARA_MONICA))) {
            seal = DNGMAP_SEAL_NONE;
        }

        if (seal == DNGMAP_SEAL_MAX && !(members & (1 << USER_CHARA_MAX))) {
            seal = DNGMAP_SEAL_NONE;
        }
    }

    return seal;
}

int CDngFloorManager::IsClearMostFastDestroy() {
    DNG_BATTLE_AREA  *scene = menu_GetBattleAreaScene();
    CSaveData        *save = GetSaveData();
    CSaveDataDungeon *dungeon = &save->save_dungeon;

    if (dungeon == NULL || scene == NULL) {
        return DNGMAP_FAST_DESTROY_NONE;
    }

    int               floor = dungeon->floor_id[dungeon->stage_id];
    DNGMAP_ROOM_INFO *info = GetDngMapFloorInfo(floor);
    DNG_FLOOR_SAVE   *saved = dungeon->GetFloorInfoPtr(dungeon->stage_id, floor);

    if (info == NULL || saved == NULL) {
        return DNGMAP_FAST_DESTROY_NONE;
    }

    int elapsed = ((int) save->play_time - (int) scene->subject_counter) * 6 / 5;
    int result = DNGMAP_FAST_DESTROY_NONE;

    if (saved->fast_destroy_time == 0) {
        if (elapsed < info->fast_destroy_time) {
            saved->fast_destroy_time = elapsed;
            result = DNGMAP_FAST_DESTROY_FIRST;
            GetUserDataMan()->AddYarikomiMedal(result);
            saved->flag |= DNG_FLOOR_FLAG_FAST_DESTROY_CLEAR;
        }
    } else if (elapsed < saved->fast_destroy_time) {
        saved->fast_destroy_time = elapsed;
        result = DNGMAP_FAST_DESTROY_RECORD;
    }

    return result;
}

int CDngFloorManager::IsClearPractice(int difficulty) {
    CSaveDataDungeon *dungeon = menu_GetSaveDataDungeon();
    DNG_BATTLE_AREA  *scene = menu_GetBattleAreaScene();
    int               floor = dungeon->floor_id[dungeon->stage_id];
    DNGMAP_ROOM_INFO *info = GetDngMapFloorInfo(floor);
    DNG_FLOOR_SAVE   *saved = dungeon->GetFloorInfoPtr(dungeon->stage_id, floor);

    if (info == NULL || saved == NULL || scene == NULL) {
        return DNGMAP_PRACTICE_NONE;
    }

    static s8 diff_conditiontable[2][DNGMAP_PRACTICE_TYPE_NUM] = {
        {1, 1, 1, 1, 1, 0, 0},
        {0, 0, 0, 0, 0, 1, 0},
    };
    static u16 check_bittable[3][6] = {
        {DNG_PRACTICE_ACTION_MAX_MELEE, DNG_PRACTICE_ACTION_MAX_GUN, DNG_PRACTICE_ACTION_MONICA_MELEE, DNG_PRACTICE_ACTION_MONICA_MAGIC, DNG_PRACTICE_ACTION_RIDEPOD, DNG_PRACTICE_ACTION_MONSTER},
        {DNG_PRACTICE_ACTION_ITEM, DNG_PRACTICE_ACTION_MAX_MELEE, DNG_PRACTICE_ACTION_MAX_GUN, DNG_PRACTICE_ACTION_MONICA_MELEE, DNG_PRACTICE_ACTION_MONICA_MAGIC, DNG_PRACTICE_ACTION_MONSTER},
        {DNG_PRACTICE_ACTION_RIDEPOD, DNG_PRACTICE_ACTION_MAX_MELEE, DNG_PRACTICE_ACTION_MAX_GUN, DNG_PRACTICE_ACTION_MONICA_MELEE, DNG_PRACTICE_ACTION_MONICA_MAGIC, DNG_PRACTICE_ACTION_ITEM},
    };
    int result;
    int mask;
    int found;
    int practice_type;
    int active;
    int r;
    int i;
    int j;
    int k;
    int l;
    int m;

    practice_type = info->practice_type;

    if (practice_type < 0) {
        return DNGMAP_PRACTICE_NONE;
    }

    active = scene->battle_clear;
    result = DNGMAP_PRACTICE_NONE;

    if (diff_conditiontable[difficulty][practice_type] == 0) {
        return DNGMAP_PRACTICE_NONE;
    }

    mask = scene->practice_actions;

    j = 0;

    while (++j < DNGMAP_PRACTICE_TYPE_NUM) {
    }

    switch (practice_type) {
        case DNGMAP_PRACTICE_TYPE_TIME:
            if (active != 0) {
                if (scene->timer < info->practice_param) {
                    result = DNGMAP_PRACTICE_CLEAR;
                }
            }

            break;
        case DNGMAP_PRACTICE_TYPE_ITEM_ONLY:
        case DNGMAP_PRACTICE_TYPE_WEAPON_ONLY:
        case DNGMAP_PRACTICE_TYPE_RIDEPOD_ONLY:
        case DNGMAP_PRACTICE_TYPE_MONSTER_ONLY:
            if (active != 0) {
                found = false;

                if (practice_type == DNGMAP_PRACTICE_TYPE_ITEM_ONLY) {
                    for (i = 0; i < 6; i++) {
                        if (mask & check_bittable[0][i]) {
                            found = true;
                        }
                    }
                }

                if (practice_type == DNGMAP_PRACTICE_TYPE_RIDEPOD_ONLY) {
                    for (k = 0; k < 6; k++) {
                        if (mask & check_bittable[1][k]) {
                            found = true;
                        }
                    }
                }

                if (practice_type == DNGMAP_PRACTICE_TYPE_MONSTER_ONLY) {
                    for (l = 0; l < 6; l++) {
                        if (mask & check_bittable[2][l]) {
                            found = true;
                        }
                    }
                }

                if (practice_type == DNGMAP_PRACTICE_TYPE_WEAPON_ONLY) {
                    if ((mask & DNG_PRACTICE_ACTION_ITEM) || (mask & DNG_PRACTICE_ACTION_RIDEPOD) || (mask & DNG_PRACTICE_ACTION_MONSTER)) {
                        found = true;
                    } else {
                        static u16 cbit[4][5] = {
                            {DNG_PRACTICE_ACTION_MAX_GUN, DNG_PRACTICE_ACTION_MONICA_MELEE, DNG_PRACTICE_ACTION_MONICA_MAGIC, DNG_PRACTICE_ACTION_RIDEPOD, DNG_PRACTICE_ACTION_MONSTER},
                            {DNG_PRACTICE_ACTION_MAX_MELEE, DNG_PRACTICE_ACTION_MONICA_MELEE, DNG_PRACTICE_ACTION_MONICA_MAGIC, DNG_PRACTICE_ACTION_RIDEPOD, DNG_PRACTICE_ACTION_MONSTER},
                            {DNG_PRACTICE_ACTION_MAX_MELEE, DNG_PRACTICE_ACTION_MAX_GUN, DNG_PRACTICE_ACTION_MONICA_MAGIC, DNG_PRACTICE_ACTION_RIDEPOD, DNG_PRACTICE_ACTION_MONSTER},
                            {DNG_PRACTICE_ACTION_MAX_MELEE, DNG_PRACTICE_ACTION_MAX_GUN, DNG_PRACTICE_ACTION_MONICA_MELEE, DNG_PRACTICE_ACTION_RIDEPOD, DNG_PRACTICE_ACTION_MONSTER},
                        };

                        r = info->practice_param - 1;

                        for (m = 0; m < 5; m++) {
                            if (mask & cbit[r][m]) {
                                found = true;
                            }
                        }
                    }
                }

                if ((mask & (1 << info->practice_param)) && !found) {
                    result = DNGMAP_PRACTICE_CLEAR;
                }
            }

            break;
        case DNGMAP_PRACTICE_TYPE_NO_HEAL:
            result = DNGMAP_PRACTICE_CLEAR;

            if (mask & DNG_PRACTICE_ACTION_HEAL) {
                result = DNGMAP_PRACTICE_FAILED;
            }

            break;
        case DNGMAP_PRACTICE_TYPE_NONE:
            break;
    }

    if (result == DNGMAP_PRACTICE_CLEAR) {
        if (saved->flag & DNG_FLOOR_FLAG_PRACTICE_CLEAR) {
            result = DNGMAP_PRACTICE_CLEAR_AGAIN;
        }
    }

    if (result == DNGMAP_PRACTICE_CLEAR || result == DNGMAP_PRACTICE_CLEAR_AGAIN) {
        saved->flag |= DNG_FLOOR_FLAG_PRACTICE_CLEAR;
    }

    if (result == DNGMAP_PRACTICE_CLEAR) {
        GetUserDataMan()->AddYarikomiMedal(1);
    }

    return result;
}

DNGMAP_ROOM_INFO *CDngFloorManager::GetDngMapFloorInfo(int floor_id) {
    if (this->glid_info == NULL) {
        return NULL;
    }

    GLID_INFO *glid = GetDngMapFloorGlidInfo(floor_id);

    if (glid != NULL) {
        return &glid->room;
    }

    return NULL;
}

DNGMAP_ROOM_INFO *CDngFloorManager::GetActiveFloorInfo() {
    CSaveDataDungeon *save = menu_GetSaveDataDungeon();

    if (save == NULL) {
        return NULL;
    }

    return GetDngMapFloorInfo(save->floor_id[save->stage_id]);
}

void CDngFloorManager::RelationGlid() {
    int        i;
    int        j;
    GLID_INFO *room;
    GLID_INFO *other;

    for (i = 0; i < glid_num; i++) {
        room = &glid_info[i];

        for (j = 0; j < glid_num; j++) {
            other = &glid_info[j];

            if (other != room) {
                if (room->x == other->x) {
                    if (other->y == room->y - 1) {
                        room->link_glid[GLID_DIR_UP] = other;
                    }

                    if (other->y == room->y + 1) {
                        room->link_glid[GLID_DIR_DOWN] = other;
                        break;
                    }
                }

                if (room->y == other->y) {
                    if (other->x == room->x - 1) {
                        room->link_glid[GLID_DIR_LEFT] = other;
                    }

                    if (other->x == room->x + 1) {
                        room->link_glid[GLID_DIR_RIGHT] = other;
                    }
                }
            }
        }
    }
}

void CDngFloorManager::CheckDrawGlidInfo() {
    CSaveDataDungeon *save;
    GLID_INFO        *next;
    GLID_INFO        *root;
    DNGMAP_ROOM_INFO *floor_room;
    DNGMAP_ROOM_INFO *next_room;
    DNG_FLOOR_SAVE   *floor;
    DNG_FLOOR_SAVE   *next_floor;
    int               i;
    int               dir;
    int               search_dir;
    int               open;
    int               opened;

    save = menu_GetSaveDataDungeon();

    if (save == NULL) {
        return;
    }

    int floor_num[DNGMAP_DUNGEON_NUM] = {9, 16, 25, 21, 23, 29, 39};

    for (int floor_no = 0; floor_no < floor_num[dng_no]; floor_no++) {
        floor = save->GetFloorInfoPtr(dng_no, floor_no);
        floor_room = GetDngMapFloorInfo(floor_no);

        if (floor != NULL && floor_room != NULL) {
            floor_room->visited = false;

            if (floor != NULL && floor->visit_count > 0) {
                floor_room->visited = true;
            }
        }
    }

    for (int n = 0; n < glid_num; n++) {
        GLID_INFO *glid = &glid_info[n];

        if (glid != NULL) {
            glid->blink = false;
        }
    }

    for (i = 0; i < glid_num; i++) {
        GLID_INFO *glid = &glid_info[i];

        if (glid->type == GLID_TYPE_ROOM) {
            DNGMAP_ROOM_INFO *room = &glid->room;

            for (dir = 0; dir < GLID_DIR_NUM; dir++) {
                next = GetNextRoom(room->floor_id, dir, NULL, -1, NULL);

                if (next != NULL && next != glid && next->type == GLID_TYPE_ROOM) {
                    save->GetFloorInfoPtr(dng_no, next->room.floor_id);
                }
            }

            room->selectable = true;
            floor = save->GetFloorInfoPtr(dng_no, room->floor_id);
            room->mark = false;

            if (floor != NULL && (floor->flag & DNG_FLOOR_FLAG_OPEN) && !(floor->flag & DNG_FLOOR_FLAG_CLEAR)) {
                room->mark = true;
            }
        }
    }

    for (i = 0; i < glid_num; i++) {
        GLID_INFO *glid = &glid_info[i];

        if (glid->type == GLID_TYPE_ROOM) {
            DNGMAP_ROOM_INFO *room = &glid->room;
            floor = save->GetFloorInfoPtr(dng_no, glid->room.floor_id);

            for (dir = 0; dir < GLID_DIR_NUM; dir++) {
                search_dir = dir;
                next = GetNextRoom(room->floor_id, dir, NULL, -1, NULL);

                if (next == NULL || next == glid || search_dir != dir) {
                    continue;
                }

                open = false;
                opened = false;
                next_room = &next->room;
                next_floor = NULL;

                if (next_room != NULL) {
                    next_floor = save->GetFloorInfoPtr(dng_no, next_room->floor_id);
                }

                if (next_floor != NULL && floor != NULL && (floor->flag & DNG_FLOOR_FLAG_OPEN) && (next_floor->flag & DNG_FLOOR_FLAG_OPEN)) {
                    open = true;
                    opened = open;
                }

                root = GetNextGlid(glid, &search_dir);

                while (root != next && root != NULL && next != NULL) {
                    if (root->type != GLID_TYPE_ROOT) {
                        break;
                    }

                    root->root.open = open != 0;
                    root->root.opened |= opened;
                    root = GetNextGlid(root, &search_dir);
                }
            }
        }
    }
}

GLID_INFO *CDngFloorManager::GetNextGlid(GLID_INFO *glid, int *index) {
    int        k;
    int       *row;
    int        current;
    GLID_INFO *result;
    GLID_INFO *room = glid;

    if (glid == NULL) {
        return NULL;
    }

    current = *index;

    if (dng_no == 2) {
        static int search_tbl[GLID_DIR_NUM][3] = {
            {GLID_DIR_UP,    GLID_DIR_LEFT,  GLID_DIR_RIGHT},
            {GLID_DIR_DOWN,  GLID_DIR_RIGHT, GLID_DIR_LEFT },
            {GLID_DIR_LEFT,  GLID_DIR_DOWN,  GLID_DIR_UP   },
            {GLID_DIR_RIGHT, GLID_DIR_UP,    GLID_DIR_DOWN },
        };
        row = search_tbl[current];
    } else if (dng_no == 3) {
        static int search_tbl[GLID_DIR_NUM][3] = {
            {GLID_DIR_UP,    GLID_DIR_LEFT,  GLID_DIR_RIGHT},
            {GLID_DIR_DOWN,  GLID_DIR_RIGHT, GLID_DIR_LEFT },
            {GLID_DIR_LEFT,  GLID_DIR_UP,    GLID_DIR_DOWN },
            {GLID_DIR_RIGHT, GLID_DIR_UP,    GLID_DIR_DOWN },
        };
        row = search_tbl[current];
    } else {
        static int search_tbl[GLID_DIR_NUM][3] = {
            {GLID_DIR_UP,    GLID_DIR_LEFT, GLID_DIR_RIGHT},
            {GLID_DIR_DOWN,  GLID_DIR_LEFT, GLID_DIR_RIGHT},
            {GLID_DIR_LEFT,  GLID_DIR_UP,   GLID_DIR_DOWN },
            {GLID_DIR_RIGHT, GLID_DIR_UP,   GLID_DIR_DOWN },
        };
        row = search_tbl[current];
    }

    for (k = 0; k < 3; k++) {
        if (room->link_glid[row[k]] != NULL) {
            *index = row[k];
            break;
        }
    }

    current = *index;
    result = NULL;

    if (0 <= current) {
        result = room->link_glid[current];
    }

    return result;
}

GLID_INFO *CDngFloorManager::GetNextRoom(int floor_id, int dir, GLID_INFO *glid, int unused,
                                         int *out_dir) {
    GLID_INFO        *room;
    int              *row;
    int               i;
    int               k;
    s16               next;
    DNGMAP_ROOM_INFO *info;

    room = GetDngMapFloorGlidInfo(floor_id);

    if (room == NULL) {
        return NULL;
    }

    if (room->type != GLID_TYPE_ROOM) {
        return NULL;
    }

    int dirs[GLID_DIR_NUM][3] = {
        {GLID_DIR_UP,    GLID_DIR_LEFT,  GLID_DIR_RIGHT},
        {GLID_DIR_DOWN,  GLID_DIR_RIGHT, GLID_DIR_LEFT },
        {GLID_DIR_LEFT,  GLID_DIR_UP,    GLID_DIR_DOWN },
        {GLID_DIR_RIGHT, GLID_DIR_DOWN,  GLID_DIR_DOWN },
    };
    row = dirs[dir];
    info = &room->room;

    i = 0;

    while (++i < GLID_DIR_NUM) {
    }

    for (k = 0; k < 2; k++) {
        next = info->link[row[k]];

        if (0 <= next) {
            room = GetDngMapFloorGlidInfo(next);

            if (out_dir != NULL) {
                *out_dir = row[k];
            }

            break;
        }
    }

    return room;
}

GLID_INFO *CDngFloorManager::GetKeyNextRoom(int floor_id, int dir, GLID_INFO *glid) {
    GLID_INFO *room = GetDngMapFloorGlidInfo(floor_id);

    if (room == NULL) {
        return NULL;
    }

    if (room->type != GLID_TYPE_ROOM) {
        return NULL;
    }

    return GetDngMapFloorGlidInfo(room->room.key_room[dir]);
}

int CDngFloorManager::GetDngMapNextFloorID(int floor_id, int root_type) {
    GLID_INFO        *room;
    GLID_INFO        *other;
    GLID_INFO        *next;
    DNGMAP_ROOM_INFO *info;
    int               d;
    DNGMAP_ROOM_INFO *other_info;
    int               index;

    room = GetDngMapFloorGlidInfo(floor_id);

    if (room == NULL) {
        return 0;
    }

    if (dng_no == 2) {
        if (floor_id == 8) {
            return 8;
        }
    }

    info = &room->room;

    for (d = 0; d < GLID_DIR_NUM; d++) {
        if (info->link[d] >= 0) {
            other = GetDngMapFloorGlidInfo(info->link[d]);

            if (other != NULL) {
                other_info = &other->room;

                if (other_info != NULL && other_info->order > info->order) {
                    index = d;
                    next = GetNextGlid(room, &index);

                    if (next != NULL && next->type == GLID_TYPE_ROOT && next->root.type == root_type) {
                        return other_info->floor_id;
                    }
                }
            }
        }
    }

    return 0;
}

char *CDngFloorManager::GetFloorTitle(int floor_id) {
    DNGMAP_ROOM_INFO *info = GetDngMapFloorInfo(floor_id);

    if (dng_no == 1 && floor_id == DNGMAP_FLOOR_SPECIAL) {
        static char *fl_t[2] = {"\x95|\x82\xA2\x90X", "Wonder Forest"};
        int          language = LanguageCode;

        if (language > LANG_ENGLISH) {
            language = LANG_ENGLISH;
        }

        return fl_t[language];
    }

    if (info == NULL) {
        return NULL;
    }

    return info->title;
}

int CDngFloorManager::GetDngMapNextRoot(int floor_id) {
    GLID_INFO        *room;
    GLID_INFO        *other;
    GLID_INFO        *next;
    int               mask;
    DNGMAP_ROOM_INFO *info;
    DNGMAP_ROOM_INFO *other_info;
    int               d;
    int               index;

    if (floor_id == DNGMAP_FLOOR_SPECIAL) {
        return 1;
    }

    room = GetDngMapFloorGlidInfo(floor_id);
    mask = 0;

    if (room == NULL) {
        return 0;
    }

    info = &room->room;

    for (d = 0; d < GLID_DIR_NUM; d++) {
        if (info->link[d] >= 0) {
            other = GetDngMapFloorGlidInfo(info->link[d]);

            if (other != NULL) {
                other_info = &other->room;

                if (other_info != NULL && other_info->order > info->order) {
                    index = d;
                    next = GetNextGlid(room, &index);

                    if (next != NULL && next->type == GLID_TYPE_ROOT) {
                        mask |= 1 << next->root.type;
                    }
                }
            }
        }
    }

    return mask;
}

int GetCountSphedaClear() {
    CSaveDataDungeon *save;
    int               count;
    int               dungeon;
    int               floor;
    DNG_FLOOR_SAVE   *info;

    save = menu_GetSaveDataDungeon();
    count = 0;

    if (save == NULL) {
        return 0;
    }

    for (dungeon = 0; dungeon < SAVE_DUNGEON_NUM; dungeon++) {
        for (floor = 0; floor < DNGMAP_FLOOR_NUM; floor++) {
            info = save->GetFloorInfoPtr(dungeon, floor);

            if (info == NULL) {
                break;
            }

            if (info->spheda_clear > 0) {
                count += 1;
            }
        }
    }

    return count;
}

int CheckFishingRecord(float size) {
    CSaveDataDungeon *save;
    CDngFloorManager *floors;
    DNG_FLOOR_SAVE   *save_info;
    DNGMAP_ROOM_INFO *info;
    int               centimeters;
    int               met;
    s8                kind;
    u16               flags;

    save = menu_GetSaveDataDungeon();

    if (save == NULL) {
        return false;
    }

    floors = &menu_GetBattleAreaScene()->floor_manager;
    save_info = save->GetFloorInfoPtr(save->stage_id, save->floor_id[save->stage_id]);
    info = floors->GetDngMapFloorInfo(save->floor_id[save->stage_id]);

    if (save_info == NULL || info == NULL) {
        return false;
    }

    kind = info->fishing;

    if (kind == 0) {
        return false;
    }

    centimeters = fptosi(100.0f * size);
    met = false;

    if (kind < 0) {
        if (centimeters <= info->fishing_record) {
            met = true;
        }
    }

    if (0 < kind) {
        if (info->fishing_record <= centimeters) {
            met = true;
        }
    }

    if (met) {
        flags = save_info->flag;

        if (!(flags & DNG_FLOOR_FLAG_FISHING_CLEAR)) {
            save_info->flag = flags | DNG_FLOOR_FLAG_FISHING_CLEAR;
            GetUserDataMan()->AddYarikomiMedal(1);
            return true;
        }
    }

    return false;
}
