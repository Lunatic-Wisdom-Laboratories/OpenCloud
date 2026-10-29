# Headless replay timing

`--headless` advances the logic clock without waiting for wall time. It also advances the complete
sound mixer from those same ticks, without opening a playback device. Voice sample levels,
stream completion and MIDI status therefore describe the same audio position at the same game tick
in every replay. This matters because event scripts use voice levels for facial animation and
stream status for waits.

Interactive runs retain device-driven audio. Setting `DC2_AUDIO_WAV` records a headless replay's
mixed sound through the existing tick-driven WAV capture; only that capture advances the mixer.
`DC2_AUDIO=off` suppresses output while headless audio state still advances for game logic.

Compare captures made with the same binary, input script, frame limit and configuration, using a
fresh save directory for every run. Mixer ownership affects voice-driven poses and expressions; device-clock and tick-clock captures
are not interchangeable. Debug-event input fixtures require `game.debug_mode=true` explicitly in
the fresh replay config; ordinary boot/title/costume fixtures use the default-off configuration.
