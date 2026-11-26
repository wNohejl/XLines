using XLines.Data.Gamecast;
using XLines.Web.Components.Snippets;
using XLines.Web.Tests.Support;

namespace XLines.Web.Tests;

/// <summary>
/// A play's headline, against sentences copied from the stored NFL plays (facts of games, a
/// handful of each type): every ESPN type the store holds heads as something sensible, a flag
/// that wiped a play out heads as the penalty, a flagged play keeps its own yards rather than the
/// net, a punt's distance is the kick and not the return, a kneel and a spike are told apart from
/// the run and the pass they are filed as, and a type nobody knows heads as nothing.
/// </summary>
public class PlayHeadlineTests
{
    private static GamecastPlay Play(string? type, string text, int? yards = 0,
        bool penalty = false, bool scoring = false, bool turnover = false)
        => FakeGamecastData.Play(1, 1, text) with
        {
            Type = type,
            Yardage = yards,
            IsPenalty = penalty,
            IsScoring = scoring,
            IsTurnover = turnover,
        };

    public static TheoryData<string?, string, int?, bool, bool, bool, string?> Plays => new()
    {
        // type, text, yardage, penalty, scoring, turnover, headline
        { "Rush", "(Shotgun) J.Cook up the middle to BUF 31 for 1 yard (N.Dean).", 1, false, false, false, "1-yard run" },
        { "Rush", "(Shotgun) O.Hampton up the middle to LA 23 for no gain (B.Fiske; W.Bailey).", 0, false, false, false, "No gain" },
        { "Rush", "O.Hampton left guard to LA 16 for -1 yards (N.Landman, J.Stewart).", -1, false, false, false, "1-yard loss" },
        { "Rush", "(Shotgun) J.Herbert scrambles up the middle to LA 23 for 4 yards (N.Landman).", 4, false, false, false, "4-yard scramble" },
        { "Rush", "B.Nix kneels to DEN 26 for -1 yards.", -1, false, false, false, "Kneel" },
        // Net −3 stored; the play itself ran 29 before the hold behind it.
        { "Rush", "(Shotgun) L.Jackson scrambles right guard to BLT 41 for 29 yards (C.Barton).PENALTY on BLT-E.Sarratt, Offensive Holding, 10 yards, enforced at BLT 29.", -3, true, false, false, "29-yard scramble" },
        { "Rush", "J.Hurts up the middle to TEN 37 for 1 yard (A.Hill).PENALTY on TEN-J.Martin, Defensive Offside, 5 yards, enforced at TEN 38 - No Play.", 5, true, false, false, "Penalty, defensive offside, 5 yards" },
        { "Rush", "K.Williams right guard for 2 yards, TOUCHDOWN NULLIFIED by Penalty.PENALTY on LA-C.Shelton, Offensive Holding, 10 yards, enforced at PHI 0.", -18, true, false, false, "Penalty, offensive holding, 10 yards" },

        { "Pass Reception", "(Shotgun) B.Young pass deep middle to X.Legette to ATL 10 for 55 yards (M.Hughes).", 55, false, false, false, "55-yard pass" },
        { "Pass Reception", "(Shotgun) S.Sanders pass short right to D.Boston to CLV 39 for no gain (J.Ramsey).", 0, false, false, false, "Pass, no gain" },
        { "Pass Reception", "(Shotgun) J.Herbert pass short left to K.Vidal to LA 49 for -2 yards (T.McDuffie).", -2, false, false, false, "Pass, 2-yard loss" },

        { "Pass Incompletion", "(Shotgun) D.Watson pass incomplete deep left to K.Concepcion (M.Fitzpatrick).", 0, false, false, false, "Incomplete pass" },
        { "Pass Incompletion", "(No Huddle) A.Rodgers spiked the ball to stop the clock.", 0, false, false, false, "Spike" },
        { "Pass Incompletion", "(Shotgun) D.Prescott pass incomplete deep left to J.Ferguson (C.DeJean).PENALTY on PHI-C.DeJean, Defensive Pass Interference, 17 yards, enforced at DAL 16 - No Play.", 17, true, false, false, "Penalty, defensive pass interference, 17 yards" },
        { "Pass Incompletion", "(Shotgun) J.Love pass incomplete short right [T.Lacy].PENALTY on GB-J.Love, Intentional Grounding, 11 yards, enforced at GB 19.", -22, true, false, false, "Penalty, intentional grounding, 11 yards" },
        { "Pass Incompletion", "Team Safety", 0, true, true, false, "Safety" },

        { "Sack", "(Shotgun) J.Brissett sacked at ARZ 24 for -10 yards (sack split by B.Dorlus and J.Pearce).", -10, false, false, false, "10-yard sack" },
        { "Sack Opp Fumble Recovery", "D.Prescott sacked at DAL 9 for -7 yards (M.Crosby). FUMBLES (M.Crosby) [M.Crosby], RECOVERED by LV-T.Hemingway at DAL 15.", 0, false, false, true, "Sack, fumble lost" },

        { "Passing Touchdown", "(Shotgun) T.Lawrence pass deep right to P.Washington for 27 yards, TOUCHDOWN. C.Little extra point is No Good, Wide Left, Center-D.Hamilton, Holder-L.Cooke.", 27, false, true, false, "Touchdown, 27-yard pass" },
        { "Rushing Touchdown", "Z.Charbonnet left end for 1 yard, TOUCHDOWN. J.Myers extra point is GOOD, Center-C.Stoll, Holder-M.Dickson.", 1, false, true, false, "Touchdown, 1-yard run" },
        { "Interception Return Touchdown", "(Shotgun) B.Mayfield pass short right intended for C.Otton INTERCEPTED by J.Johnson at CHI 33. J.Johnson for 67 yards, TOUCHDOWN. C.Santos extra point is GOOD, Center-S.Daly, Holder-T.Taylor.", 67, false, true, true, "Touchdown, 67-yard interception return" },
        { "Fumble Return Touchdown", "James Mitchell 0 Yd Fumble Recovery (Brandon Aubrey Kick)", 0, false, true, false, "Touchdown, fumble return" },
        { "Punt Return Touchdown", "M.Dickson punts 60 yards to TEN 10, Center-C.Stoll. C.Dike for 90 yards, TOUCHDOWN. J.Slye extra point is GOOD, Center-M.Cox, Holder-J.Hekker.", 90, false, true, false, "Touchdown, 90-yard punt return" },
        { "Kickoff Return Touchdown", "Z.Gonzalez kicks 65 yards from ATL 35 to SEA 0. R.Shaheed for 100 yards, TOUCHDOWN. J.Myers extra point is GOOD, Center-C.Stoll, Holder-M.Dickson.", 100, false, true, false, "Touchdown, 100-yard kickoff return" },
        // Called back: not a score, and the flags were offsetting, so no foul reads cleanly.
        { "Kickoff Return Touchdown", "J.Elliott kicks 55 yards from PHI 35 to JAX 10. J.Cameron for 90 yards, TOUCHDOWN NULLIFIED by Penalty.Penalty on PHI-A.Epenesa, Illegal Formation, offsetting, enforced at PHI 35 - No Play.", 90, true, false, false, "Penalty" },
        { "Blocked Punt Touchdown", "Marshawn Kneeland Blocked Punt Recovery in End Zone (Brandon Aubrey Kick)", 0, false, true, true, "Touchdown, blocked punt" },
        { "Blocked Field Goal Touchdown", "Jared Verse 76 Yd Return of Blocked Field Goal (Harrison Mevis Kick)", 76, false, true, true, "Touchdown, blocked field goal" },

        { "Pass Interception Return", "(Shotgun) D.Jones pass deep left intended for M.Pittman INTERCEPTED by K.Curl at LA 44. K.Curl ran ob at LA 44 for no gain.", 0, false, false, true, "Interception" },
        { "Fumble Recovery (Opponent)", "(Shotgun) A.Gibson up the middle to PIT 42 for 1 yard (T.Watt; J.Peppers). FUMBLES (T.Watt), RECOVERED by PIT-J.Peppers at PIT 43.", 0, false, false, true, "Fumble lost" },
        { "Fumble Recovery (Own)", "J.Dart FUMBLES (Aborted) at NYG 5, recovered by NYG-A.Thomas at NYG 1.", -4, false, false, false, "Fumble, recovered" },
        { "Fumble Recovery (Own)", "Eric Saubert 13 Yd pass from Sam Darnold (Jason Myers Kick)", 13, false, true, false, "Touchdown" },
        { "Muffed Punt Recovery (Opponent)", "T.Townsend punts 42 yards to DEN 25, Center-A.Brinkman. M.Bandy MUFFS catch, RECOVERED by HST-J.Smith at DEN 30.", 0, false, false, false, "Muffed punt" },

        { "Field Goal Good", " B.Aubrey 24 yard field goal is GOOD, Center-T.Sieg, Holder-B.Anger.", 24, false, true, false, "Field goal, 24 yards" },
        { "Field Goal Good", " J.Myers 27 yard field goal is GOOD, NULLIFIED by Penalty, Center-C.Stoll, Holder-M.Dickson.\nPENALTY on JAX-D.Smoot, Defensive Holding, 5 yards, enforced at JAX 9 - No Play.", 27, true, false, false, "Penalty, defensive holding, 5 yards" },
        { "Field Goal Missed", "C.Little 54 yard field goal is No Good, Wide Left, Center-R.Matiscik, Holder-L.Cooke.", 54, false, false, false, "Missed field goal, 54 yards" },
        // The stored figure is the recovery's; the distance is the sentence's.
        { "Blocked Field Goal", "B.McManus 43 yard field goal is BLOCKED (S.Harris), Center-M.Orzech, Holder-D.Whelan, RECOVERED by CLV-G.Newsome at CLV 47.", 43, false, false, true, "Blocked field goal, 43 yards" },
        { "Blocked Field Goal", "C.McLaughlin 38 yard field goal is BLOCKED (C.Jackson), Center-E.Deckers, Holder-R.Dixon.", 0, false, false, true, "Blocked field goal, 38 yards" },

        // The stored 12 is the return; the punt went 50.
        { "Punt", "R.Sanchez punts 50 yards to DAL 13, Center-L.Rhodes. J.Mingo pushed ob at DAL 25 for 12 yards (A.Adebawore).", 12, false, false, false, "Punt, 50 yards" },
        { "Punt", "J.Scott punts 55 yards to BLT 25, Center-J.Harris, fair catch by L.Wester.", 0, false, false, false, "Punt, 55 yards" },
        { "Blocked Punt", "E.Evans punt is BLOCKED by I.Simmons, Center-J.McQuaide, recovered by LA-R.Rivers at LA 30.", 0, false, false, true, "Blocked punt" },

        { "Kickoff", "K.Fairbairn kicks 65 yards from HST 35 to end zone, Touchback to the TB 35.", 0, false, false, false, "Kickoff, touchback" },
        { "Kickoff", "A.Szmyt kicks 60 yards from CLV 35 to HST 5. N.Remigio to HST 37 for 32 yards (D.McMillon).", 32, false, false, false, "Kickoff, 32-yard return" },
        { "Kickoff", "N.Folk kicks onside 10 yards from NYJ 35 to NYJ 45. D.Waller (didn't try to advance) to NYJ 45 for no gain.", 10, false, false, false, "Onside kick" },

        { "Penalty", "(Shotgun) C.Williams pass short right to R.Odunze to NE 35 for 4 yards (M.Jones) [G.Jacas].PENALTY on CHI-G.Bradbury, Offensive Holding, 10 yards, enforced at NE 39 - No Play.", -10, true, false, false, "Penalty, offensive holding, 10 yards" },
        { "Penalty", "TWO-POINT CONVERSION ATTEMPT. B.Purdy pass to M.Evans is incomplete. ATTEMPT FAILS.\nPENALTY on SEA-J.Jobe, Defensive Pass Interference, 1 yard, enforced at SEA 2 - No Play.", 1, true, false, false, "Penalty, defensive pass interference, 1 yard" },
        { "Penalty", "(Shotgun) D.Prescott pass short right intended for J.Ferguson INTERCEPTED by N.Dean [Z.Baun] at PHI 37. N.Dean to DAL 45 for 18 yards (T.Steele).Penalty on PHI, Defensive Too Many Men on Field, offsetting, enforced at PHI 37.", 0, true, false, false, "Penalty" },
        { "Penalty", "(Shotgun) M.Jones sacked at SF 6 for 0 yards (Ma.Wilson).PENALTY on SF-D.Puni, Offensive Holding, 6 yards, enforced in End Zone, SAFETY - No Play.", -6, true, true, false, "Safety" },

        { "Safety", "(Shotgun) D.Jones sacked in End Zone for -7 yards, SAFETY (W.Anderson).", -7, false, true, false, "Safety" },
        { "Defensive 2pt Conversion", "Zyon McCollum Defensive PAT Conversion", 0, false, true, false, "Defensive two-point conversion" },

        { "Timeout", "Timeout #2 by GB at 12:58.", 0, false, false, false, "Timeout, GB" },
        { "Official Timeout", "Official Timeout at 14:14.", 0, false, false, false, "Official timeout" },
        { "Two-minute warning", "Two-Minute Warning", 0, false, false, false, "Two-minute warning" },
        { "End Period", "END QUARTER 1", 0, false, false, false, "End of quarter" },
        { "End of Half", "END QUARTER 2", 0, false, false, false, "End of half" },
        { "End of Regulation", "END QUARTER 4", 0, false, false, false, "End of regulation" },
        { "End of Game", "END GAME", 0, false, false, false, "End of game" },

        // Nothing to say: a row the feed left untyped, and a type this table does not know.
        { null, "Markquese Bell Defensive PAT Conversion", 0, false, true, false, null },
        { "Jump Shot", "J.Tatum makes 18-foot jumper", null, false, false, false, null },
    };

    [Theory]
    [MemberData(nameof(Plays))]
    public void A_play_heads_as_its_type_and_yards_say(
        string? type, string text, int? yards, bool penalty, bool scoring, bool turnover, string? expected)
        => Assert.Equal(expected, PlayHeadline.For(Play(type, text, yards, penalty, scoring, turnover)));

    [Fact]
    public void The_type_is_read_whatever_its_case()
        => Assert.Equal("16-yard pass", PlayHeadline.For(Play("pass reception", "pass to A.Brown for 16 yards", 16)));

    [Fact]
    public void A_flagged_play_whose_sentence_states_no_yards_is_named_without_a_number()
        => Assert.Equal("Run", PlayHeadline.For(Play("Rush", "J.Gibbs right tackle.PENALTY on DET-P.Sewell, Offensive Holding, 10 yards, enforced at NYJ 32.", -10, penalty: true)));

    [Fact]
    public void A_punt_whose_sentence_gives_no_distance_is_just_a_punt()
        => Assert.Equal("Punt", PlayHeadline.For(Play("Punt", "Punt", 0)));
}
