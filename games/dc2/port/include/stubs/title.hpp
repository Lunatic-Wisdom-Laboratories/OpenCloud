#pragma once

extern unsigned char GameBootInit asm("Title_GameBootInit");

#include <title.hpp>
extern TITLE_INFO   *TitleInfo asm("MenuMouse_Title");
extern short         TitlePhase asm("MenuMouse_TitlePhase");
extern unsigned char TitleHDDCheckFlag asm("MenuMouse_TitleHDD");
extern unsigned char TitleMCCheckNow asm("Title_MCCheckNow");
class ClsMes;
extern ClsMes *TitleMCCheckMes asm("Title_MCCheckMes");
int            PortRetailTitleCopyRightStep() asm("Title_TitleCopyRightStep");
int            PortRetailTitleLoop() asm("Title_TitleLoop");
void           PortRetailTitleModeDraw() asm("Title_TitleModeDraw");
