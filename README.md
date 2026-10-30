# Global Autocorrect

**Release candidate 1 — not yet confirmed on your PC (13.3).**

Global Autocorrect tidies up what you type, as you type, in almost any Windows app. It runs entirely on your laptop, using your NVIDIA GPU. No text ever leaves the machine.

- **Obvious slips are fixed the moment you finish a word.** `teh` becomes `the`, and `bookk` becomes `book`.
- **Everything else is fixed by a small AI model that reads the surrounding sentence.** That includes run-together or split words, missing apostrophes and capitals, punctuation and grammar slips. It runs when you finish a sentence or pause for a moment.
- **It fixes mistakes, not your style.** It never swaps your words for synonyms, rephrases you, or changes British spellings to American ones.
- **A faint suggestion after the caret finishes your word or predicts the next one.** Press **Tab** to take it, or just keep typing to ignore it.

## What it doesn't do

- It never touches password fields, windows running as administrator, or the apps on its excluded list: terminals, code editors, password managers, remote desktop, games and Excel cells. You can edit that list.
- It never corrects pasted or dictated text, or text inserted by other tools. It only corrects what you type yourself.
- It keeps no history or list of corrections. Only counts are kept, such as "corrections today".
- English (UK) only, on a UK keyboard layout.

## Shortcuts

| What | Default | Change it in |
|---|---|---|
| Pause or resume | **Ctrl+Alt+P**, or one click on the tray icon | Settings › Shortcuts |
| Undo the last correction, straight after it | **Ctrl+Z** | — |
| Undo the last correction, from anywhere in that field | **Ctrl+Alt+Z** | Settings › Shortcuts |
| Take the suggestion | **Tab**, only while it's showing | Settings › Prediction (on or off) |

Undoing a correction also adds that word to your personal dictionary, so it won't be corrected again.

## Install

1. Install the .NET 10 runtime (already on this PC).
2. Unzip `GlobalAutocorrect-<version>.zip` somewhere permanent, for example `C:\Apps\GlobalAutocorrect`.
3. Run `app\GlobalAutocorrect.exe`. The first-run window explains what it does and downloads the model, about 6 GB, with a progress bar. You can also download it from a terminal:
   ```
   pwsh scripts/setup.ps1
   ```
4. Check it's using the GPU:
   ```
   pwsh scripts/verify.ps1
   ```
   Every check prints OK, or explains the fix in plain language.

It starts with Windows (you can turn that off in Settings) and lives in the system tray.

## Windows Defender

A program that watches the keyboard and types for you can look like a keylogger to antivirus software. If Global Autocorrect disappears after you start it:

1. Open **Windows Security › Virus & threat protection › Protection history**.
2. Choose the Global Autocorrect entry and pick **Allow on device**.
3. Optionally add the app folder under **Virus & threat protection settings › Exclusions › Add an exclusion › Folder**.

`pwsh scripts/verify.ps1` checks for this too.

## Measured accuracy and speed

Measured on 200 test messages that were never used for tuning. These are everyday UK English messages with realistic typing slips added, plus hand-mangled ones. The whole pipeline was scored: instant fixes, the AI and the safety rules together. Measured 30 October 2026 with Gemma 4 E4B on the RTX PRO 2000, after a second round of tuning on the separate tuning set (the same 200 test messages were also scored once before that round, at 92.3%). The full report is `eval-report.md` in this folder.

| | Result | Target |
|---|---|---|
| Mistakes fixed | **92.9%** | 95% ❌ (not met) |
| Correct words wrongly changed | **0.08%** (about 1 in 1,300) | under 1% ✅ |
| Messages left exactly as intended | 87% | — |
| Time for an AI clean-up | 361 ms typical, 535 ms slowest 5% | under 2 s ✅ |

**Where it falls short.** Light slips are fixed 94% of the time and heavily mangled messages 97%. Messages with many different slips at once are fixed 88% of the time. Most misses are:
- a dropped small word the model won't guess ("make stand-up" instead of "make the stand-up");
- a slip that turns one real word into another, or into a name ("dun" became "Dunn" instead of "sun");
- a space typed one letter too early, which splits one word and joins its last letter onto the next.

It prefers leaving a mistake to changing something you meant. That's why wrong changes are so rare.

## Apps tested

| App | How it was tested | Result |
|---|---|---|
| Notepad (Windows 11) | Spike B; end-to-end tests: AI fix, Tab suggestion, works alongside the autocorrect built into Notepad | ✅ (see note) |
| Word | Spike B | ✅ |
| Edge: plain text box | Spike B; end-to-end tests | ✅ (see note) |
| Edge: rich text box | Spike B; end-to-end tests | ✅ (see note) |
| Edge: ProseMirror editor | Spike B; end-to-end tests | ✅ (see note) |
| Edge: Lexical editor | Spike B; end-to-end tests | ✅ (see note) |
| WPF text box | End-to-end tests: instant fix, Ctrl+Z undo and learning the word | ✅ (see note) |
| Password boxes | End-to-end test | ✅ never touched |
| Intercom, Claude.ai, ChatGPT in Edge | Not automated: they need your signed-in accounts. Their message boxes are rich web editors like the ones above | ⚠️ Please try: a typo, a "their/there" sentence, Ctrl+Z straight after a fix, Tab on a suggestion |
| Outlook (classic) | Spike B could not put the cursor in the message body automatically | ⚠️ Not verified. Its message body is the same editor as Word, which passes |

Note: the end-to-end results for this release candidate are in `e2e-results.txt` in this folder.

## Known limits

- **Accuracy is below the 95% target** (92.9% of mistakes fixed). It is weakest on messages with many slips at once, and on a missing small word such as "the", which it rarely puts back.
- **It only works where the app reports its text and cursor position to Windows.** Most apps do. Where an app doesn't, Global Autocorrect does nothing rather than guess.
- **It never corrects across a line break**, and only looks at the current paragraph.
- **Some apps also autocorrect.** If Word, Outlook or Notepad changes a word itself, Global Autocorrect notices and stays out of the way, so it may skip that correction.
- **Code editors are excluded by default.** In VS Code and similar editors, only about a page of text is visible to other apps.
- **Changing the AI model takes effect after a restart.**
- **Other GPU-heavy apps can slow clean-ups down.** The model needs about 3.5 GB of the 8 GB of graphics memory. If games, video tools or other AI apps take the rest, Windows quietly moves graphics memory into ordinary memory and clean-ups can take seconds instead of a third of a second. `scripts/verify.ps1` measures this.
- **Ctrl+Alt+Z undoes from anywhere in the field that was corrected**, not from another window. If you've moved to another window, it tells you to switch back.
- **A shortcut can be taken by another app.** If Ctrl+Alt+P or Ctrl+Alt+Z is already in use, the tray tells you and you can choose another in Settings.
- **Corrections are typed as ordinary key presses** (D22 in `DECISIONS.md`). The brief asks for Unicode keystrokes, but Notepad scrambled those, so this is flagged for you to confirm.
- **UK English and a UK keyboard layout only.**

## Requirement coverage

Every requirement in the brief, with the tests that prove it or the recorded check (2.11, 11.2). Generated by `scripts/coverage.ps1`; "Recorded check" items are verified by a script, a review or a measured run, as described.

| ID | Status | Proof |
|---|---|---|
| 0.1 | Recorded check | eval report shows errors fixed / false edits (M6) |
| 0.2 | Tested (incl. E2E) | 5 test(s), e.g. `R0_2_a_RealDictionariesFixTehAndBookk` |
| 0.2.a | Tested (incl. E2E) | 3 test(s), e.g. `R0_2_a_RealDictionariesFixTehAndBookk` |
| 0.2.b | Tested (incl. E2E) | 9 test(s), e.g. `R11_3_a_NotepadAiFix_R0_2_b` |
| 0.2.c | Tested | 1 test(s), e.g. `R0_2_c_AppliesEveryAcceptedEdit` |
| 0.2.d | Tested | 18 test(s), e.g. `R4_4_d_NoFullStopAddedToAnUnfinishedSentence` |
| 0.3 | Tested (incl. E2E) | 19 test(s), e.g. `R7_1_AllCapsPartialGetsUpperCaseCompletion` |
| 0.4 | Tested | 1 test(s), e.g. `R3_4_InterfaceTextIsBritishAndSpeltCorrectly_R0_4` |
| 0.5 | Recorded check | milestone order in PROGRESS |
| 0.6 | Recorded check | DECISIONS.md raised items |
| 1.1 | Recorded check | `Directory.Build.props` net10.0 |
| 1.2 | Recorded check | package refs WPF-UI 4.3.0 / Tray |
| 1.3 | Tested | 3 test(s), e.g. `R5_1_b_BindsToLoopbackWithApiKeyAndNoSlots` |
| 1.4 | Recorded check | Â§4.1 spike table |
| 1.5 | Tested | 3 test(s), e.g. `R1_5_FreqDictLoads` |
| 1.6 | Tested | 3 test(s), e.g. `R7_2_AnswersUnder10ms` |
| 1.7 | Tested (incl. E2E) | 8 test(s), e.g. `R10_7_a_ArrowKeysAreSentAsTheNavigationCluster` |
| 1.8 | Recorded check | test project refs |
| 1.9 | Tested | 7 test(s), e.g. `R3_3_LoopbackOnly` |
| 1.10 | Recorded check | no CI files in repo |
| 2.1 | Recorded check | process |
| 2.2 | Recorded check | BRIEF.md first commit 0850a00 |
| 2.2.a | Recorded check | commit 0850a00, pushed |
| 2.2.b | Recorded check | `git log -- BRIEF.md` shows one commit |
| 2.2.c | Recorded check | PROGRESS notes "BRIEF re-read" per milestone |
| 2.3 | Recorded check | this file |
| 2.3.a | Recorded check | PROGRESS prerequisites entry |
| 2.3.b | Recorded check | this file |
| 2.3.c | Recorded check | `scripts/trace-check.ps1` runs in the fast tier |
| 2.3.d | Recorded check | Â§4 |
| 2.3.e | Recorded check | DECISIONS.md |
| 2.3.f | Recorded check | commit of PLAN.md |
| 2.4 | Recorded check | Â§4.1 |
| 2.4.a | Recorded check | Â§4.1 Spike A table + `artifacts/spikeA-*.json` |
| 2.4.b | Recorded check | Â§4.1 Spike B table |
| 2.4.c | Recorded check | Â§4 pinned + Â§4.1 |
| 2.5 | Recorded check | PROGRESS |
| 2.5.a | Recorded check | PROGRESS records fast green per milestone |
| 2.5.b | Recorded check | PROGRESS |
| 2.5.c | Recorded check | git log messages carry IDs |
| 2.6 | Recorded check | process |
| 2.6.a | Recorded check | stop on 2026-10-29 for .NET 10 |
| 2.6.b | Recorded check | process |
| 2.6.c | Recorded check | process |
| 2.6.d | Recorded check | D1 (tag conflict raised) |
| 2.6.e | Recorded check | RC stop message |
| 2.7 | Recorded check | PROGRESS next-task field |
| 2.8 | Recorded check | E2E `Desk.RequireIdle` and Spike B `InputGuard` (D20); runs recorded in PROGRESS |
| 2.9 | Recorded check | process |
| 2.10 | Tested | 1 test(s), e.g. `R2_10_NoWordSpecificCorrectionTable` |
| 2.11 | Recorded check | README coverage table |
| 2.12 | Recorded check | process |
| 2.12.a | Recorded check | `.claude/agents/implementer.md` |
| 2.12.b | Recorded check | owner column in Â§8 |
| 2.12.c | Recorded check | PROGRESS delegation log |
| 2.12.d | Recorded check | this file Â§6, Â§8 |
| 2.12.e | Recorded check | PROGRESS review notes |
| 3.1 | Recorded check | spike hardware notes |
| 3.2 | Recorded check | no installer/signing |
| 3.3 | Tested | 1 test(s), e.g. `R3_3_LoopbackOnly` |
| 3.4 | Tested | 6 test(s), e.g. `R3_4_AmericanSpellingsAndTyposAreInvalid` |
| 3.5 | Recorded check | PROGRESS prerequisites entry |
| 3.6 | Recorded check | CLAUDE.md, .claude/settings.json |
| 4.1 | Tested | 2 test(s), e.g. `R4_1_a_NoAppIsSpecialCasedOutsideTheExclusionList` |
| 4.1.a | Tested | 1 test(s), e.g. `R4_1_a_NoAppIsSpecialCasedOutsideTheExclusionList` |
| 4.1.b | Tested | 1 test(s), e.g. `R4_1_b_CaretUnknownMeansNothingIsWritten` |
| 4.2 | Tested | 4 test(s), e.g. `R4_2_ForeignInsertedTextBreaksTheSessionAndIsNotCorrected` |
| 4.3 | Tested | 15 test(s), e.g. `R4_3_a_FixesTheFinishedWordWithoutWaiting` |
| 4.3.a | Tested | 2 test(s), e.g. `R4_3_a_FixesTheFinishedWordWithoutWaiting` |
| 4.3.b | Tested | 1 test(s), e.g. `R4_3_b_InstantLayerNeverCallsTheModel` |
| 4.3.c | Tested | 8 test(s), e.g. `R4_3_c_AdjacentSwapIsAnObviousFix` |
| 4.3.d | Tested | 4 test(s), e.g. `R4_3_d_CloseCandidatesAreLeftForTheAi` |
| 4.4 | Tested | 26 test(s), e.g. `R4_4_a_DetectsSentenceEnd` |
| 4.4.a | Tested | 4 test(s), e.g. `R4_4_a_DetectsSentenceEnd` |
| 4.4.b | Tested | 2 test(s), e.g. `R4_4_b_SendsCurrentSentencePlusTwoBefore` |
| 4.4.c | Tested | 8 test(s), e.g. `R4_4_c_ClassifiesApostropheCapitalisationPunctuation` |
| 4.4.d | Tested | 4 test(s), e.g. `R4_4_d_NoFullStopAddedToAnUnfinishedSentence` |
| 4.4.e | Tested | 8 test(s), e.g. `R4_4_e_HeavyGarbleStillDiffsWordByWord` |
| 4.5 | Tested | 14 test(s), e.g. `R4_5_a_AcceptsCharacterSimilarWord` |
| 4.5.a | Tested | 7 test(s), e.g. `R4_5_a_AcceptsCharacterSimilarWord` |
| 4.5.b | Tested | 1 test(s), e.g. `R4_5_b_AcceptsFunctionWordInsertionButNotContentWords` |
| 4.5.c | Tested | 1 test(s), e.g. `R4_5_c_RejectsOneEditButKeepsTheRest` |
| 4.5.d | Tested | 4 test(s), e.g. `R4_5_d_NeverRespacesNumbers` |
| 4.5.e | Tested | 1 test(s), e.g. `R4_5_e_SettingsAreConfigurable` |
| 4.6 | Tested | 5 test(s), e.g. `R4_6_b_LastTwoWordsWaitForA1500msPause_R4_6_a` |
| 4.6.a | Tested | 1 test(s), e.g. `R4_6_b_LastTwoWordsWaitForA1500msPause_R4_6_a` |
| 4.6.b | Tested | 1 test(s), e.g. `R4_6_b_LastTwoWordsWaitForA1500msPause_R4_6_a` |
| 4.6.c | Tested | 1 test(s), e.g. `R4_6_c_NoAiWriteWhileKeysKeepComing_ThenRebasesAfterAGap` |
| 4.6.d | Tested | 2 test(s), e.g. `R4_6_d_DesyncStopsCorrectingThatField` |
| 4.6.e | Tested | 1 test(s), e.g. `R4_6_e_WriteWaitsButTypingDoesNot` |
| 4.7 | Tested | 12 test(s), e.g. `R9_3_a_CorrectionsTodayResetsAtMidnightWhileRunning_R6_5` |
| 4.8 | Tested (incl. E2E) | 10 test(s), e.g. `R4_8_a_CtrlZRevertsTheCorrection_R4_8_c` |
| 4.8.a | Tested (incl. E2E) | 5 test(s), e.g. `R4_8_a_CtrlZRevertsTheCorrection_R4_8_c` |
| 4.8.b | Tested | 5 test(s), e.g. `R4_8_a_HeldModifiersAreReleasedForTheBatchAndRestoredAfter_R4_8_b` |
| 4.8.c | Tested (incl. E2E) | 2 test(s), e.g. `R4_8_a_CtrlZRevertsTheCorrection_R4_8_c` |
| 5.1 | Tested | 3 test(s), e.g. `R5_1_b_BindsToLoopbackWithApiKeyAndNoSlots` |
| 5.1.a | Recorded check | setup extracts cudart beside llama-server; `R5_1_a_*` |
| 5.1.b | Tested | 1 test(s), e.g. `R5_1_b_BindsToLoopbackWithApiKeyAndNoSlots` |
| 5.1.c | Tested | 1 test(s), e.g. `R5_1_c_FallsBackToCpuBuildWhenCudaWontStart` |
| 5.1.d | Tested | 1 test(s), e.g. `R5_1_d_LlamaSharpIsNotUsed` |
| 5.2 | Tested | 3 test(s), e.g. `R5_2_ModelIsASettingDefaultingToTheSpikeAWinner` |
| 5.3 | Tested | 1 test(s), e.g. `R5_3_c_CpuFallbackUsesTheSmallerModel` |
| 5.3.a | Recorded check | Â§4.1 |
| 5.3.b | Recorded check | Â§4.1 |
| 5.3.c | Tested | 1 test(s), e.g. `R5_3_c_CpuFallbackUsesTheSmallerModel` |
| 5.3.d | Recorded check | Â§4.1 default chosen |
| 5.4 | Tested | 1 test(s), e.g. `R5_4_ThinkingDisabled` |
| 5.5 | Tested | 1 test(s), e.g. `R5_5_MedianUnder2sOnGpu` |
| 5.6 | Tested (incl. E2E) | 7 test(s), e.g. `R5_6_a_AFailedStartIsNotRetriedOnEveryKeystroke` |
| 5.6.a | Tested | 3 test(s), e.g. `R5_6_a_AFailedStartIsNotRetriedOnEveryKeystroke` |
| 5.6.b | Tested | 1 test(s), e.g. `R5_6_b_UnloadsAfterIdleAndReloadsOnNextKey` |
| 5.6.c | Tested | 1 test(s), e.g. `R5_6_c_OnlyOneCopyRuns` |
| 5.7 | Recorded check | setup.ps1 run log; `R5_7_ManifestHasChecksums` |
| 5.8 | Tested | 3 test(s), e.g. `R5_8_a_NoCudaLineMeansNotUsingGpu` |
| 5.8.a | Tested | 3 test(s), e.g. `R5_8_a_NoCudaLineMeansNotUsingGpu` |
| 5.8.b | Recorded check | verify.ps1 |
| 5.8.c | Recorded check | verify.ps1 |
| 5.8.d | Recorded check | verify.ps1 |
| 5.8.e | Tested | 1 test(s), e.g. `R5_8_e_CleansATestSentence_R5_8_a` |
| 5.8.f | Recorded check | verify.ps1 |
| 5.8.g | Recorded check | verify.ps1 |
| 6.1 | Tested | 8 test(s), e.g. `R6_1_a_MessagesValid` |
| 6.1.a | Tested | 1 test(s), e.g. `R6_1_a_MessagesValid` |
| 6.1.b | Tested | 4 test(s), e.g. `R6_1_b_EachOpTypeCanOccur` |
| 6.1.c | Tested | 1 test(s), e.g. `R6_1_c_FiftyGarbledMessages` |
| 6.1.d | Tested | 1 test(s), e.g. `R6_1_d_AtLeast20PercentClean` |
| 6.1.e | Tested | 1 test(s), e.g. `R6_1_e_HoldoutAtLeast200AndDisjoint` |
| 6.2 | Recorded check | eval runs `CorrectionEngine` |
| 6.3 | Tested | 7 test(s), e.g. `R6_3_a_DoubledWordInNoiseNeedsFix` |
| 6.3.a | Tested | 3 test(s), e.g. `R6_3_a_DoubledWordInNoiseNeedsFix` |
| 6.3.b | Tested | 2 test(s), e.g. `R6_3_b_FalseEditCounted` |
| 6.3.c | Tested | 1 test(s), e.g. `R6_3_c_ExactMatchRateAggregates` |
| 6.3.d | Recorded check | metrics.json |
| 6.3.e | Tested | 1 test(s), e.g. `R6_3_e_EmptyAggregateIsSafe` |
| 6.4 | Recorded check | eval-report.md targets |
| 6.5 | Tested | 3 test(s), e.g. `R6_5_CountsCarryAcrossRestartsAndEarlierDaysAreKept` |
| 6.6 | Recorded check | About page + README numbers |
| 7.1 | Tested (incl. E2E) | 7 test(s), e.g. `R7_1_AllCapsPartialGetsUpperCaseCompletion` |
| 7.2 | Tested | 3 test(s), e.g. `R7_2_AnswersUnder10ms` |
| 7.3 | Tested | 5 test(s), e.g. `R7_3_HistoryBoostsTheUsersOwnWords` |
| 7.4 | Tested (incl. E2E) | 4 test(s), e.g. `R7_4_ArmIsOneShotAndOnlyForTheVeryNextKey_R4_8_a` |
| 7.5 | Tested | 1 test(s), e.g. `R7_5_NoSuggestionAfterNonMatchingText` |
| 7.6 | Tested (incl. E2E) | 2 test(s), e.g. `R10_3_d_OverlayIsClickThroughNoActivateAndNotInAltTab_R7_6` |
| 7.7 | Tested | 1 test(s), e.g. `R7_7_DisabledPredictorSuggestsNothing` |
| 8.1 | Tested | 2 test(s), e.g. `R8_1_StartupRegistrationDefaultsToTheUserRunKey` |
| 8.2 | Tested | 3 test(s), e.g. `R8_2_DefaultPauseIsCtrlAltP` |
| 8.3 | Tested (incl. E2E) | 4 test(s), e.g. `R8_3_AcceptingASuggestionNeverTypesIntoAFieldItMayNotRead_R7_4` |
| 8.4 | Tested | 4 test(s), e.g. `R8_4_DefaultsExcludeTerminalsEditorsPasswordManagersRemoteDesktop` |
| 8.5 | Tested | 8 test(s), e.g. `R8_5_AddContainsRemoveRoundTripsThroughFile` |
| 8.6 | Tested | 7 test(s), e.g. `R8_6_AutoLearnCountsAreNotPersisted` |
| 9.1 | Recorded check | screenshots |
| 9.2 | Tested | 5 test(s), e.g. `R9_2_a_Tokens_xaml_matches_Palette` |
| 9.2.a | Tested | 1 test(s), e.g. `R9_2_a_Tokens_xaml_matches_Palette` |
| 9.2.b | Recorded check | screenshots |
| 9.2.c | Tested | 1 test(s), e.g. `R9_2_c_Tokens_xaml_defines_type_scale_spacing_and_radii` |
| 9.2.d | Tested | 3 test(s), e.g. `R9_2_d_ContrastAtLeast4_5` |
| 9.2.e | Recorded check | icon screenshots on light/dark taskbar |
| 9.3 | Tested | 12 test(s), e.g. `R9_3_a_CorrectionsTodayResetsAtMidnightWhileRunning_R6_5` |
| 9.3.a | Tested | 3 test(s), e.g. `R9_3_a_CorrectionsTodayResetsAtMidnightWhileRunning_R6_5` |
| 9.3.b | Tested | 3 test(s), e.g. `R9_3_b_BoldnessMapsToTheSimilarityThreshold` |
| 9.3.c | Tested | 1 test(s), e.g. `R9_3_c_PredictionSwitchIsSaved` |
| 9.3.d | Tested | 1 test(s), e.g. `R9_3_d_ShortcutRecordingRejectsInvalidCombos` |
| 9.3.e | Tested | 1 test(s), e.g. `R9_3_e_ExcludedAppsAddRemoveRestore` |
| 9.3.f | Tested | 1 test(s), e.g. `R9_3_f_DictionaryAddRemoveViaViewModel` |
| 9.3.g | Tested | 2 test(s), e.g. `R9_3_g_AboutReadsMetricsJson` |
| 9.4 | Tested | 6 test(s), e.g. `R9_4_FirstRunMarksDoneOnFinish` |
| 9.5 | Recorded check | `artifacts/screens` + PROGRESS review notes |
| 10.1 | Tested | 1 test(s), e.g. `R10_1_CoreHasNoWindowsReferences` |
| 10.2 | Tested | 2 test(s), e.g. `R10_2_HostAutoCapitalisationIsAdoptedNotABreak` |
| 10.3 | Tested | 11 test(s), e.g. `R10_3_a_HookInstallsAndRemovesWithoutBlocking` |
| 10.3.a | Tested (incl. E2E) | 5 test(s), e.g. `R10_3_a_HookInstallsAndRemovesWithoutBlocking` |
| 10.3.b | Tested | 3 test(s), e.g. `R10_3_b_AfterAHangItBacksOffInsteadOfLeakingAThreadPerKey` |
| 10.3.c | Tested | 1 test(s), e.g. `R10_7_a_OneBatchMovesLeftDeletesTypesAndMovesBack_R10_3_c` |
| 10.3.d | Tested | 1 test(s), e.g. `R10_3_d_OverlayIsClickThroughNoActivateAndNotInAltTab_R7_6` |
| 10.3.e | Tested | 5 test(s), e.g. `R10_3_e_ParsedValuesMatchRegisterHotKeyConstants` |
| 10.4 | Recorded check | App project |
| 10.5 | Tested | 6 test(s), e.g. `R10_5_CancellationPropagates` |
| 10.6 | Tested | 2 test(s), e.g. `R10_6_b_ReadsAWindowAroundTheCaretNotTheWholeDocument` |
| 10.6.a | Recorded check | Spike B (Â§4.1): TextPattern2 in Notepad/Word, selection fallback in Chromium |
| 10.6.b | Tested | 1 test(s), e.g. `R10_6_b_ReadsAWindowAroundTheCaretNotTheWholeDocument` |
| 10.6.c | Tested | 1 test(s), e.g. `R10_7_c_NeverUsesValuePatternSetValue_R10_6_c` |
| 10.6.d | Tested (incl. E2E) | 1 test(s), e.g. `R11_3_b_InstantAndAiFixesInBrowserEditors` |
| 10.7 | Tested | 4 test(s), e.g. `R10_7_a_ArrowKeysAreSentAsTheNavigationCluster` |
| 10.7.a | Tested | 2 test(s), e.g. `R10_7_a_ArrowKeysAreSentAsTheNavigationCluster` |
| 10.7.b | Tested | 1 test(s), e.g. `R10_7_b_EveryInjectedEventCarriesOurTag` |
| 10.7.c | Tested | 1 test(s), e.g. `R10_7_c_NeverUsesValuePatternSetValue_R10_6_c` |
| 10.8 | Tested | 3 test(s), e.g. `R10_8_GateReportsElevated` |
| 11.1 | Recorded check | check.ps1 |
| 11.1.a | Recorded check | fast timing in PROGRESS |
| 11.1.b | Recorded check | check.ps1 model run |
| 11.1.c | Recorded check | check.ps1 eval run |
| 11.1.d | Recorded check | check.ps1 e2e run |
| 11.1.e | Recorded check | check.ps1 full run |
| 11.2 | Recorded check | `scripts/coverage.ps1` generates the README table |
| 11.3 | Tested (incl. E2E) | 4 test(s), e.g. `R11_3_a_NotepadAiFix_R0_2_b` |
| 11.3.a | Tested (incl. E2E) | 3 test(s), e.g. `R11_3_a_NotepadAiFix_R0_2_b` |
| 11.3.b | Tested (incl. E2E) | 1 test(s), e.g. `R11_3_b_InstantAndAiFixesInBrowserEditors` |
| 11.3.c | Recorded check | E2E typer uses SendInput |
| 11.3.d | Recorded check | E2E assertion order |
| 11.4 | Recorded check | cache path; `.gitignore` |
| 12.1 | Recorded check | E2E Notepad inputs (`teh`, `bookk`, `their`) are words Notepad leaves alone; Spike B Notepad run |
| 12.2 | Tested | 1 test(s), e.g. `R12_2_NoWriteWhenAKeyArrivedSinceTheDecision` |
| 12.3 | Tested | 1 test(s), e.g. `R12_3_AiRunsWhileUserTypes_TypingIsNotBlocked` |
| 12.4 | Tested | 3 test(s), e.g. `R9_2_d_ContrastAtLeast4_5` |
| 12.5 | Recorded check | lexical.html |
| 12.6 | Tested | 1 test(s), e.g. `R10_3_a_HookInstallsAndRemovesWithoutBlocking` |
| 12.7 | Recorded check | verify.ps1 |
| 12.8 | Tested | 1 test(s), e.g. `R5_4_ThinkingDisabled` |
| 12.9 | Recorded check | README known limits |
| 12.10 | Recorded check | verify.ps1 5.8.g; README |
| 13.1 | Recorded check | package zip contents |
| 13.2 | Tested | 1 test(s), e.g. `R13_2_ReadmeIsBritishAndSpeltCorrectly` |
| 13.3 | Recorded check | no v1.0.0 tag (D1) |
| 13.4 | Recorded check | timed setup + verify run |
| 14.1 | Out of scope (14) | Out of scope |
| 14.2 | Out of scope (14) | Out of scope |
| 14.3 | Out of scope (14) | Out of scope |
| 14.4 | Tested | 4 test(s), e.g. `R4_2_ForeignInsertedTextBreaksTheSessionAndIsNotCorrected` |
| 14.5 | Tested (incl. E2E) | 9 test(s), e.g. `R4_8_a_CtrlZRevertsTheCorrection_R4_8_c` |
| 14.6 | Out of scope (14) | Out of scope |
| 14.7 | Out of scope (14) | Out of scope |
| 14.8 | Out of scope (14) | Out of scope |
| 14.9 | Tested | 4 test(s), e.g. `R4_4_d_NoFullStopAddedToAnUnfinishedSentence` |

## For developers

See `CLAUDE.md` for build and test commands, `PLAN.md` for the design, `DECISIONS.md` for every judgement call, and `PROGRESS.md` for status.
