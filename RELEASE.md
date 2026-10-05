## Notes for the next release

- Fix: PTT got stuck on if a player talked during a reconnect.
  8aa50cc, 1588f4a

- Fix: Smooth physics params so that jumps in distance and angular velocity
  (especially in close formation) don't cause distortions.
  c3d535f

- Improve mic level normalization, reduce input and output clipping.
  bdb5373

- Experiment: Add a log-crunching script to determine when folks step on each others' comms.
  Binaries will be provided Soon™.
  6cf1f96, b76c0f6, 3b0e8ec
