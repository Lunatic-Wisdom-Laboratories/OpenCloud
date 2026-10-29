#pragma once

struct NowLoadingInfo;
extern NowLoadingInfo LoadInfo asm("NowLoad_LoadInfo");
extern int            ProgBarCnt asm("NowLoad_ProgBarCnt");
extern float          ProgBarWidthStep asm("NowLoad_ProgBarWidthStep");

// game.skip_intro, which SCElogoFade's fade loop checks.
bool PortSkipIntro();

struct PAUSE_INFO;
int PortRetailPauseStart(PAUSE_INFO *) asm("NowLoad_PauseStart");
