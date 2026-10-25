# Logic-native preview policy (1.5.0)

This policy supersedes the earlier all-PST rejection behavior. It applies to
desktop, CLI, recursive batch and localhost web previews.

- A recognized Logic-native PST **instrument** skips the entire `.patch`.
  Detection checks every inspected strip, including additional strips in a
  stack, before starting the AU host. No substitute instrument or silent WAV
  is produced.
- A recognized PST **audio effect**, with a confirmed wrapper role, is omitted.
  The remaining supported instrument and AU effects render in their original
  order. Reports list omitted effects by slot/name and state that the preview
  is approximate. This omission applies even when metadata approximation is off.
- Classification uses saved-state format and wrapper role, not the word
  “factory”, a filename or the Apple manufacturer code. Public Apple AUs such
  as the supported Sampler/low-pass test chain are retained.
- Missing third-party AUs, ambiguous roles, native or AU MIDI effects, unreadable
  states, sends and unsupported routing are not silently discarded. These
  remain errors. Stacks without a detected native instrument remain unsupported.

## Approximation defaults

**Logic AU 链近似** defaults to on for new desktop configurations, the web form,
web requests without a supplied value, and CLI renders. An explicitly saved
desktop value of false remains false. To change that saved preference, check
the Logic Patch row's checkbox and save settings.

CLI `--strict` disables omission of uninterpreted channel/mixer metadata;
`--au-chain-only` enables it. Neither flag allows native instruments to render,
and both omit recognized native audio effects as requested. Other unsupported
routing/plugin checks still apply. Approximation does not establish equivalence
to Logic's full channel-strip sound.

## Skip reporting and preservation

Batch reports/journals use `skipped_unsupported` for native instruments and count
them separately from failures. Batches continue to the next patch. A run with
only successful renders and deliberate skips exits successfully; actual errors
still cause a failing exit status. A single-patch CLI invocation reports the
skip and exits successfully without attempting playback.

The web table labels native-instrument skips, and its “Skipped / existing”
counter includes deliberate skips and existing WAVs. Skipped patches have no
audio player/download. Omitted effect details appear on rendered result rows.

The desktop marks a newly skipped instrument as unsupported, records the reason
and counts it as skipped rather than failed. It consumes no render retry attempts.
Existing previews remain preserved if an explicit regeneration discovers an
unsupported instrument. Existing batch WAVs retain their normal preserve/skip
behavior. No preexisting audio is removed by this policy change.

Rendered previews keep the existing no-overwrite, source-hash, note timing,
finite-audio and clipping checks. Silent notes still require changing the test
range or checking plugin resources; dropping effects does not bypass validation.

Tests inspect the actual native-worker manifest to confirm PST effects are absent
and Apple AUs are retained. Batch/web tests check skip accounting, and desktop
queue tests check no retries and preservation of prior previews. Mac CI wraps
real Apple AU states with a synthetic PST effect to check omission through the
desktop worker, plus a synthetic native-instrument package to check the skip
protocol. These fixtures test the policy; they do not run proprietary Logic
instruments or certify every factory patch.
