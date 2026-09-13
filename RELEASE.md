## Release notes for vNext

- Fix volume getting quieter when adjusting in-cockpit knobs
  4f99bc9

- Fix several bugs that could cause permanent deafness at high altitudes
  e6fe46f, adbc59a

- Added logging for when people start and stop talking, including callsigns and BMS game time
  6b536db

- Reduce up to 100ms delay on PTT to about 1/60s
  (also 6b536db)

- Fix race conditions around transmitting frequencies.
  (Also fixes a UI bug that acted as though users could transmit from multiple places at once
  on the same frequency, but picked one arbitrarily.)
  36f948f

- Fixed deadlocks and related disconnects between the UI and BMS shared memory reads
  d178e21

- Increase Windows tick rate for the process to improve responsiveness
  cc3ceea
