## Release notes for vNext (1.1?)

- Improve terrain accuracy by building a [quadtree](https://en.wikipedia.org/wiki/Quadtree)
  of the theater and adaptively sampling it.
  (https://github.com/UOAF/OpenFreqAudio/pull/28)

- Improve terrain interference using the Delta-Bullington model
  (also https://github.com/UOAF/OpenFreqAudio/pull/28)

- Improve noise model so that static sounds different based on frequency,
  and no longer has a tremolo based on Doppler shift
  5bd6438fe7c32a45e4045be5d1cefa1d4f65b4f8

- Save recordings to Ogg Opus instead of Ogg Vorbis.
  (Expect much smaller file sizes, using the same codec used for network comms.)
  31fbbbecc9a3c92a373903249b6525e5ee74d2b2

- Integrate carrier frequency phase to prevent audible pops from phase jumps
  when the frequency changes (e.g., due to Doppler shifts)
  https://github.com/UOAF/OpenFreqAudio/commit/49f2ede1cb3023f9c0b6821a26c6706f1078407f

- Other playback fixes which could reduce popping
  https://github.com/UOAF/OpenFreqAudio/commit/81c83282c1377b2e049478a8f5463ab96f109b4d

- Fix race conditions which could cause players to permanently be unable to hear each other
  9948cad70229fa984efec96c8da999b338bd756a

- UI 2D -> 3D state fixes
  7744bddf55c9a7a52693e2c8ff91e59cb047812d

- Use monotonic clocks for network and UI timing
  8df5a6fca9cf8789249d2f30164fe97219acf1f2

- Add logs of path losses based on distance and terrain
  f95adfe46cfe7a0347c9c7c3e4e562e7b3c80bce

- Add logs of mic level and normalization
  3ff4ecd7ddec2cac29a6b8196ed5431dac27c371

