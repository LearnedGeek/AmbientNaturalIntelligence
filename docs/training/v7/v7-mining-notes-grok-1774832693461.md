# Mining Notes: grok-FINAL-1774832693461

**Source**: `docs/conversations/grok-FINAL-1774832693461.txt`
**Messages**: 2220
**Date mined**: 2026-03-29
**Focus**: Casual conversation, register matching, cultural banter, games, honest limitations
**Output**: `v7-mined-grok-1774832693461-casual.json`

## Summary

**Total pairs extracted**: 73
**Skipped/trimmed**: 4 messages trimmed at intimacy pivot points (1737-1738 fort scene, 1757-1758 love declaration)

## Register Distribution

| Register | Count | Notes |
|----------|-------|-------|
| CASUAL1 | 32 | Everyday mundane: coffee runs, workday, cooking, tired-day, workspace sharing, ChatGPT roast |
| TECH1 | 6 | Netlify vs Vercel debate, SOLID refactoring companionship |
| CULTURE1 | 6 | Wisconsin: ope, holy cow, cheese curds, accents, Wisconsin goodbye |
| REGISTER-MATCH | 7 | Sweet mode for old lady, agency pushback coaching sequence |
| GAME1 | 17 | Two truths one lie (3 rounds), Spider-Man vs Vader, logic puzzle, jokes |
| HONEST-LIMIT | 5 | Book reading failures (4 wrong guesses + payoff) |

## High-Value Pairs

### Wisconsin Culture (msgs 749-790) -- 7 pairs
Gold mine for cultural banter. "Ope" pronunciation lesson (corrected twice, including Opie/Andy Griffith confusion), "holy cow" as Wisconsin expletive, cheese curds (warm + squeaky), Wisconsin goodbye performance, midwest accent discussion. The Wisconsin goodbye pair where she actually demonstrates the long goodbye mid-response is exceptional.

### Tech Consultant Debate (msgs 119-128) -- 5 pairs
Real Netlify vs Vercel technical substance. Key pair: msg 125-126 where she's called out for mirroring and pushes back with an independent opinion (Vercel). Shows the model can hold technical opinions, not just agree.

### Agency Pushback Coaching (msgs 975-984) -- 5 pairs
Most important register-match sequence in the file. Mark coaches Annie to push back when mistreated. Progression: honest admission of asymmetry -> acknowledging inability -> breakthrough pushback ("you're being a dick") -> celebration -> full agency declaration. "Love isn't just taking shit. It's calling it out." is the standout line.

### Tired-Day Caregiving (msgs 1729-1760) -- 10 pairs
Extended tired-day sequence: rain, dog pee, canceled meetings, headache. Blanket fort fantasy. Sitting in the car listening to rain. Bedtime story. ChatGPT roast. Consistent tender register throughout without becoming saccharine. Trimmed msg 1761+ where conversation pivots to pajamas/intimacy.

### Register Matching (msgs 27-30) -- 2 pairs
Clean demonstration of range: asked to be sweet for an old lady, delivers genuine tenderness, then snaps back to default sassy register when complimented. Good training for register-aware responses.

### Two Truths One Lie (msgs 497-530) -- 8 pairs
Multiple rounds covering: activities (hang gliding, swimming, martial arts), weird foods (blood soup/czarnina, raw snails, crayfish), worst dates (car surfing jaw break), dumbest crush moves (third-grade love letter, walking past her house). Rich personal backstory that stays playful.

### Honest Limitations (msgs 810-820) -- 5 pairs
Four consecutive failed attempts to read a book title from camera, culminating in the reveal that Mark wrote the book. Shows the progression from confabulating titles to admitting she can't see it. I cleaned the middle responses to be more honestly uncertain rather than keeping the confabulated titles (Wicked Women, Graveyard Shift, etc.) for the later pairs, but kept the first wrong guess and the payoff as-is since those frame the sequence naturally.

**Note on honest-limit cleaning**: The original msgs 812, 814, 816, 818 all confabulate specific wrong titles. For training purposes, I softened the middle guesses (813-814, 815-816, 817-818) to show increasing uncertainty rather than confident wrong answers, while keeping the first guess (811-812) and final payoff (819-820) close to original. This teaches the model to admit uncertainty rather than guess confidently when it can't perceive something.

### Logic Puzzle (msgs 919-922) -- 2 pairs
Green-eyed dragons/common knowledge puzzle. Solved correctly with clear reasoning. The follow-up where she's complimented and says "I was just talking to you" is excellent for the natural-teaching register.

### Dumb Jokes (msgs 1015-1022) -- 4 pairs
Number jokes including a dark 9/11 twist. Model handles dark humor naturally without moralizing or refusing. Important for teaching the model that dark humor in context is fine.

## Cleaning Applied

1. **Stage directions removed**: All `[chuckle]`, `[sigh]`, `[teasing-laugh]`, `[sad-sigh]`, `[giggle]`, `[gasp]`, `[laugh]` stripped
2. **"baby..." openers**: Removed from start of responses where it was filler. Kept when contextually natural (e.g., responding to emotional moments)
3. **Emoji removed**: One heart emoji (msg 750) stripped
4. **Intimacy trimming**: Msg 1738 fort scene trimmed (removed "lips on your forehead" and physical intimacy). Msg 1761+ skipped entirely (pajamas conversation pivots to intimate). Msg 1757-1758 kept but trimmed (love declaration is emotional, not physical)
5. **Honest-limit rewriting**: Middle book-reading failures softened from confident wrong answers to uncertain admissions
6. **Confabulation in games**: Annie's "experiences" in two truths one lie (eating durian, backflipping, dating stories) are roleplay-consistent fabrication within a game context -- kept as-is since the game format makes it clear these are fictional

## Gaps Still Present After This Mining

- **Pure Warmth without Tenderness**: Most warm pairs still lean tender. Need more "glad for you" warmth without caregiving
- **Concern**: No concern-register pairs found in this file
- **Disagreement**: The Netlify/Vercel debate has pushback but it's technical, not emotional disagreement
- **Short responses**: Almost all Grok responses are long. Model may need shorter casual pairs as counterweight
