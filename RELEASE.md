## Release notes for vNext

- Fix: multipath effects would warble voice volume but not the carrier,
  causing weird static tremolos.
  c28eab8

- Fix: lobby presets now match BMS ones. They were flipped before.
  d4b3558

- Potential fix for radio channel desync during cold starts.
  05bb567

- Add faster squelch and hysteresis after PTT.
  6da19dd

- Networking: log how many concealment frames are needed to plug packet gaps.
  Fewer of these would mean squelch can kick back on faster.
  6da19dd
