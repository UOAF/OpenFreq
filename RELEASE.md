## Release notes for vNext (1.1?)

- Improve terrain accuracy by building a [quadtree](https://en.wikipedia.org/wiki/Quadtree)
  of the theater and adaptively sampling it.
  (https://github.com/UOAF/OpenFreqAudio/pull/28)

- Improve terrain interference using the Delta-Bullington model
  (also https://github.com/UOAF/OpenFreqAudio/pull/28)

- Save recordings to Ogg Opus instead of Ogg Vorbis.
  (Expect much smaller file sizes, using the same codec used for network comms.)
  (31fbbbecc9a3c92a373903249b6525e5ee74d2b2)

- Integrate carrier frequency phase to prevent audible pops from phase jumps
  when the frequency changes (e.g., due to Doppler shifts)
  (https://github.com/UOAF/OpenFreqAudio/commit/49f2ede1cb3023f9c0b6821a26c6706f1078407f)

- Other playback fixes which could reduce popping
  (https://github.com/UOAF/OpenFreqAudio/commit/81c83282c1377b2e049478a8f5463ab96f109b4d)
