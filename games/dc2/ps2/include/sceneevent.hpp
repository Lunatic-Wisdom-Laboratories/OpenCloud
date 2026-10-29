#pragma once
#include "common.h"

#include <cstring>
#include <libvu0.h>

#include "map.hpp"
#include "mapload.hpp"

/**
 * @file
 * Declares the description of an event that the player has reached in a
 * scene: the event point of a map, a villager to talk to or a game object,
 * which a scene keeps while it runs the event and hands to the event editor.
 */

/**
 *
 * Pair of floating point values in the scene event data.
 *
 */
struct EventFloat2 {
    float v[2]; /**< Floating point values. */
};

/**
 *
 * Four floating point values in the scene event data.
 *
 */
struct EventFloat4 {
    float v[4]; /**< Floating point values. */
};

/**
 *
 * Pair of quadwords in the scene event data.
 *
 */
struct EventVector2 {
    u_long128 v[2]; /**< Quadword values. */
};

/**
 *
 * Four quadwords in the scene event data.
 *
 */
struct EventVector4 {
    u_long128 v[4]; /**< Quadword values. */
};

/**
 *
 * Event the player has reached, with the settings, placement and owner of the point that starts it.
 *
 */
#pragma push
#pragma cpp_extensions on

struct CSceneEventData {
    union {
        struct {
            CFuncPoint::EventData event;      /**< Settings of the event point, or the event number of a villager or game object. */
            sceVu0FVECTOR         position;   /**< Position of the event point or game object. */
            sceVu0FVECTOR         rotation;   /**< Rotation of the event point, zero for a game object. */
            sceVu0FVECTOR         scale;      /**< Scale of the event point. */
            MapEventInfo          map_event;  /**< Event point found on a map, with the matrix that places it. */
            int                   chara_no;   /**< Character number of the villager talked to. */
            int                   chara_slot; /**< Scene character slot of the villager talked to. */
            int                   gameobj_no; /**< Index of the game object position within its map's entry. */
            int                   unk_cc;
        };

        struct {
            EventFloat4  head;                                 /**< First four words of the event point's settings. */
            EventFloat4  group_1;                              /**< Next four words of the event point's settings. */
            EventFloat2  group_2;                              /**< Last two words of the event point's settings. */
            EventFloat4  group_3 __attribute__((aligned(16))); /**< Position of the event point or game object. */
            EventFloat4  group_4;                              /**< Rotation of the event point. */
            EventFloat4  group_5;                              /**< Scale of the event point. */
            EventVector4 vectors_a;                            /**< First four quadwords of the map event information. */
            EventVector2 vectors_b;                            /**< Last two quadwords of the map event information. */
        };
    };

    /**
     *
     * Clears every event value before the scene fills the event description.
     *
     */
    CSceneEventData() { memset(this, 0, sizeof(*this)); }
};

#pragma pop

STATIC_ASSERT(sizeof(CSceneEventData) == 0xD0);
