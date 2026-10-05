#!/bin/bash
export PATH=/dist:$PATH
Xvfb :99 -screen 0 1280x720x24 >/dev/null 2>&1 & sleep 1; export DISPLAY=:99
omtx bars --omtx > /dev/null 2>&1 & sleep 1
SDL_VIDEO_FULLSCREEN_DISPLAY=0 omtx-play-wrapper omtx://127.0.0.1:6400 high & sleep 6
import -window root /shots/wrap.png
SDL_VIDEO_FULLSCREEN_DISPLAY=0 omtx-play-wrapper omtx://127.0.0.1:6400 medium --window 640x360+20+20 & sleep 5
import -window root /shots/wrap2.png
