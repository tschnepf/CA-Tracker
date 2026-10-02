# Real-project test cases

Each `*.json` file here is an anonymized sample exported from a real Helix project
(Settings → General → Developer tools → Export a Helix test sample in CA Tracker; the file is saved in
`%LOCALAPPDATA%\SMC-CA-Tracker\exports`). `items` is what CA Tracker read; `expected` is whose court
each item is in. When CA Tracker gets one wrong, correct its `holders` in `expected` and keep the file:
`fixtures.test.js` then checks every future change against it.

Samples from live projects can stay out of the repo: `fixtures.test.js` also reads `*.json` from the
folder named by the `CA_TRACKER_TEST_DATA` environment variable, or `C:\Local\CA-Tracker-Test-Data`
when that folder exists. The export keeps item numbers, statuses and dates, so check a sample for
names before putting it here.
