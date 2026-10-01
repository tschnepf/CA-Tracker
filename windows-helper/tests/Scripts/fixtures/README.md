# Real-project test cases

Each `*.json` file here is an anonymized sample exported from a real Helix project
(Settings → Export a Helix test sample in CA Tracker). `items` is what CA Tracker read; `expected`
is whose court each item is in. When CA Tracker gets one wrong, correct its `holders` in `expected`
and keep the file here: `fixtures.test.js` then checks every future change against it.
