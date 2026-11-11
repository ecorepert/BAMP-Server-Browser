@echo off
rem Clean stop: the mod saves the game, removes it from the list, then closes the game.
set "DATA=%USERPROFILE%\AppData\LocalLow\Hovgaard Games\Big Ambitions\BAMPServerBrowser"
type nul > "%DATA%\stop"
echo Stop requested. The server saves, then closes (a few seconds).
