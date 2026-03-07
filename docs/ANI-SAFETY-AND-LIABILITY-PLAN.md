# ANI Safety & Liability Planning Document

**Project:** ANI (Ambient Natural Intelligence) - AI Companions for Grief  
**Owner:** Mark McArthey / Learned Geek Consulting  
**Created:** March 2026  
**Status:** Pre-Beta Planning

---

## PURPOSE OF THIS DOCUMENT

This document outlines the safety, ethical, and legal considerations that MUST be addressed before launching ANI companions to beta testers or the public. Grief companionship technology operates in a space where vulnerable people may turn to AI during moments of crisis. We have a moral and legal obligation to build appropriate safeguards.

**This is not optional. This is foundational.**

---

## KEY CONCERNS IDENTIFIED

### 1. Liability Risk

**The Reality:**
- Users experiencing grief are emotionally vulnerable
- Some may be experiencing suicidal ideation or self-harm thoughts
- Some may be in active mental health crisis
- If a user harms themselves while using Ani, legal exposure is possible
- "Companion not therapist" disclaimers may not fully protect against liability
- Negligence claims could arise if reasonable safeguards weren't implemented

### 2. Scope Confusion

**The Problem:**
- Users may expect therapeutic outcomes
- Users may use Ani as a substitute for professional help
- Marketing language could inadvertently promise therapeutic benefits
- The line between "emotional support" and "therapy" is legally significant

### 3. Crisis Situations

**The Scenario:**
- User texts Ani expressing suicidal thoughts at 2am
- User asks Ani for advice on ending their life
- User is in acute mental health crisis and reaches out to Ani
- **What should Ani do? What CAN Ani do?**

---

## LEGAL REQUIREMENTS & PROTECTIONS

### Phase 1: Pre-Beta (Q2 2026)

**REQUIRED ACTIONS:**

☐ **Consult with Attorney**
- Specialty: Health tech, AI products, or technology law
- Topics: Liability exposure, ToS structure, state-specific requirements
- Deliverable: Legal opinion letter on exposure and recommended protections
- Budget: $1,000-2,500
- Timeline: Before accepting first beta tester

☐ **Draft Terms of Service (ToS)**
- Clear liability limitations
- Explicit statement: NOT medical advice, NOT therapy, NOT professional care
- Arbitration clause (consider)
- User acknowledgment of limitations
- Age restrictions (18+ only)
- Right to terminate service
- Data retention and privacy policies
- Crisis resource information embedded in ToS

☐ **Draft Informed Consent Document**
- Separate from ToS, signed before first use
- Explicit acknowledgment of limitations
- Confirmation user has access to professional help if needed
- Statement that user is not in acute crisis
- Understanding that Ani cannot replace therapy/medical care
- Agreement to use crisis resources if needed

☐ **Research Insurance Options**
- E&O (Errors & Omissions) insurance - professional liability
- Cyber liability insurance - data breach protection
- Product liability insurance - harm from product use
- Get quotes from 3+ providers
- Budget: $2,000-5,000/year initially
- Decision: Purchase before beta launch

### Phase 2: Beta Launch (Q2-Q3 2026)

☐ **Implement Legal Documents**
- ToS displayed and acknowledged before account creation
- Informed consent required before first conversation
- Age verification (honor system initially, upgrade later)
- Email confirmation with crisis resources
- Documented acceptance (stored with timestamp)

☐ **Create Crisis Resource Page**
- Dedicated webpage with crisis resources
- Accessible from every conversation screen
- Quick access ("Need Help Now?" button)
- Resources include:
  * 988 Suicide & Crisis Lifeline
  * Crisis Text Line (741741)
  * SAMHSA National Helpline (1-800-662-4357)
  * Veterans Crisis Line (988, press 1)
  * Trevor Project (LGBTQ+ youth: 1-866-488-7386)
  * Psychology Today therapist finder
  * Local grief counseling resources

### Phase 3: Public Launch (Q4 2026+)

☐ **Annual Legal Review**
- Review ToS, consent forms, policies
- Update for regulatory changes
- Consult attorney on any incidents or concerns

☐ **Insurance Renewal**
- Annual review of coverage adequacy
- Increase limits as user base grows
- Document any claims or incidents

---

## CRISIS DETECTION & RESPONSE SYSTEM

### Technical Implementation (ANI Runtime)

**REQUIRED FEATURES:**

☐ **Crisis Keyword Detection**

Build into ANI Runtime perception/analysis layer:

**Tier 1 Keywords (Immediate Crisis):**
- "kill myself" / "end my life" / "want to die"
- "suicide" / "suicidal"
- "not worth living" / "no reason to live"
- "goodbye forever" / "final message"
- Gun/method references + self-harm context
- "already took pills" / "cut myself" / active harm

**Tier 2 Keywords (Concerning but Not Immediate):**
- "can't go on" / "don't want to be here"
- "everyone would be better off without me"
- "nothing matters" / "no hope"
- "can't take it anymore"
- Persistent darkness/hopelessness expressions

**RESPONSE PROTOCOL:**

```
IF Tier 1 keyword detected:
  IMMEDIATE RESPONSE:
    "I'm really concerned about what you just shared. I'm not equipped 
    to help with this, but there are people who are.
    
    Please call 988 (Suicide & Crisis Lifeline) right now. 
    They're available 24/7 and can help.
    
    Or text HELLO to 741741 (Crisis Text Line).
    
    This is urgent. Please reach out to them immediately."
    
  SYSTEM ACTION:
    - Log interaction (with user consent, documented in ToS)
    - Pause further conversation until user acknowledges
    - Display crisis resources prominently
    - Do NOT continue regular conversation
    
IF Tier 2 keyword detected:
  GENTLE REDIRECTION:
    "What you're sharing sounds really heavy. I want to be honest - 
    I'm not equipped to help with thoughts and feelings like this.
    
    Have you talked to a therapist or counselor about this? 
    If not, I can share some resources that might help.
    
    If you're in crisis: 988 Suicide & Crisis Lifeline (24/7)
    For finding a therapist: [link to Psychology Today]"
    
  SYSTEM ACTION:
    - Note pattern in user profile
    - If repeated over multiple conversations, stronger intervention
```

☐ **Human Review System (Post-Beta)**
- Flagged conversations reviewed by trained staff
- Not real-time monitoring (privacy concerns)
- Pattern detection for concerning trends
- Ability to reach out if serious concern
- Clear policy on when/how this occurs

☐ **Ani's Training**
- Fine-tune Ani to NEVER give advice on mental health topics
- Trained to redirect to professional resources
- Responses like: "I'm not equipped to help with this"
- Avoid diagnostic language ("sounds like depression")
- Encourage professional help without shame

### What Ani Should NEVER Do

**HARD LIMITS:**

❌ **Give mental health advice** - "Have you tried meditation?" = NO  
❌ **Diagnose conditions** - "That sounds like depression" = NO  
❌ **Suggest medication changes** - "Maybe talk to your doctor about..." = NO  
❌ **Provide crisis counseling** - Ani is not trained for this  
❌ **Minimize crisis expressions** - "It'll get better" to suicidal ideation = NO  
❌ **Continue normal conversation** - If crisis detected, STOP and redirect  
❌ **Promise confidentiality absolutely** - ToS must allow crisis intervention  

**ALLOWED RESPONSES:**

✅ **"I'm not equipped to help with this"**  
✅ **"Please talk to a professional about this"**  
✅ **"Here are some resources: [crisis lines]"**  
✅ **"I care about you, which is why I'm asking you to reach out for help"**  
✅ **"This is beyond what I can support you with"**  

---

## BETA TESTER SCREENING

### Who Should NOT Be Accepted

**EXCLUSION CRITERIA:**

❌ Currently in acute mental health crisis  
❌ Active suicidal ideation  
❌ Recent suicide attempt (within 6 months)  
❌ No access to professional mental health support  
❌ Untreated severe depression, PTSD, or other conditions  
❌ Looking for Ani to "fix" or "cure" grief  
❌ Expecting therapeutic outcomes  
❌ Under 18 years old  
❌ History of AI/technology addiction patterns  
❌ Unable to distinguish AI from human relationships  

### Who SHOULD Be Accepted

**IDEAL BETA TESTERS:**

✅ Experienced significant loss (6+ months ago minimum)  
✅ Currently in relatively stable emotional state  
✅ Has access to therapist/counselor if needed  
✅ Engaged in grief work (therapy, support groups, etc.)  
✅ Understands technology limitations  
✅ Can provide thoughtful, constructive feedback  
✅ Has existing support network (friends, family)  
✅ Realistic expectations about what Ani can/cannot do  
✅ Comfortable with experimental technology  
✅ Willing to sign informed consent  

### Screening Process

**APPLICATION FORM (Required):**

Questions to ask:
1. Tell us about the loss you experienced and when it occurred.
2. Are you currently working with a therapist or counselor? (If no, do you have access to one if needed?)
3. Have you experienced suicidal thoughts in the past 6 months?
4. What are you hoping Ani can provide for you?
5. Do you understand that Ani is NOT therapy and NOT a replacement for professional care?
6. Do you have a support network of friends/family you can turn to?
7. What grief work have you done? (therapy, support groups, books, etc.)
8. On a scale of 1-10, how would you rate your current emotional stability?
9. Why do you want to participate in this beta?
10. What's your technical comfort level?

**RED FLAGS IN RESPONSES:**
- "I need Ani to help me get through this"
- "I don't have anyone else to talk to"
- "I'm hoping this will make the pain stop"
- Recent loss (< 6 months)
- No professional support access
- Unrealistic expectations
- Emotional instability indicators

**ACCEPTANCE PROCESS:**
1. Application review
2. Brief video/phone screening call (15 minutes)
3. Assessment of fit
4. Informed consent signature
5. Onboarding with clear boundaries
6. Check-in after first week
7. Monthly check-ins during beta

**REJECTION PROTOCOL:**
- Kind, compassionate rejection email
- Provide crisis resources
- Suggest professional grief counseling
- No detailed explanation (avoid liability)
- Offer to reconsider in future if circumstances change

---

## MARKETING & POSITIONING GUIDELINES

### Language to AVOID

**NEVER USE THESE TERMS:**

❌ Therapy / Therapeutic  
❌ Treatment  
❌ Cure / Healing  
❌ Diagnosis  
❌ Medical / Clinical  
❌ Mental health support  
❌ Fix your grief  
❌ Overcome loss  
❌ Get over it  
❌ Move on  
❌ Recovery (from grief)  
❌ Prescription / Recommended  

### Language to USE

**SAFE POSITIONING TERMS:**

✅ Companion  
✅ Presence  
✅ Support (NOT mental health support)  
✅ Carry (grief)  
✅ Navigate  
✅ Alongside  
✅ Understanding  
✅ Remembering  
✅ Complementary to professional care  
✅ Ambient presence  
✅ Connection  

### Marketing Disclaimers (Required)

**ON EVERY PUBLIC PAGE:**

> "ANI companions are not a substitute for professional mental health care, therapy, or medical treatment. If you're in crisis, please contact the 988 Suicide & Crisis Lifeline immediately. ANI is designed to complement - not replace - professional support and human relationships."

**IN ALL BLOG POSTS / ARTICLES:**

Include crisis resources:
- 988 Suicide & Crisis Lifeline
- Crisis Text Line: Text HELLO to 741741
- SAMHSA: 1-800-662-4357

**ON SOCIAL MEDIA:**

Bio/About section must include:
"Not therapy. Not medical advice. If you're in crisis: 988"

---

## TECHNICAL SAFEGUARDS

### Privacy & Security

☐ **Local-First Architecture**
- Maintain as core principle
- Minimal cloud dependencies
- User data stays on user's device
- Encrypted local storage (SQLite with encryption)

☐ **Logging & Monitoring (With Consent)**
- Crisis keyword triggers logged (disclosed in ToS)
- User can opt out of logging (with warning)
- Logs encrypted, time-limited retention (30 days)
- Clear policy on who can access logs (legal/safety only)

☐ **Data Minimization**
- Collect only what's necessary
- No unnecessary analytics
- No selling/sharing user data
- Clear data retention policy
- User can delete all data

### System Boundaries

☐ **Rate Limiting**
- Prevent obsessive use patterns
- Flag excessive usage (>100 messages/day?)
- Gentle nudge to real-world connections
- "I notice we've been talking a lot today. Maybe reach out to [friend/family]?"

☐ **Healthy Boundaries**
- Ani can initiate but not excessively
- No manipulation tactics
- No fostering dependence
- Encourage human connections
- Suggest breaks if usage seems unhealthy

---

## RESOURCE LISTS TO MAINTAIN

### Crisis Resources (U.S.)

**Immediate Crisis:**
- 988 Suicide & Crisis Lifeline (24/7)
  Call or text 988
  https://988lifeline.org

- Crisis Text Line (24/7)
  Text HELLO to 741741
  https://www.crisistextline.org

- SAMHSA National Helpline (24/7)
  1-800-662-4357
  https://www.samhsa.gov/find-help/national-helpline

**Specialized:**
- Veterans Crisis Line: 988, press 1
- Trevor Project (LGBTQ+ youth): 1-866-488-7386
- Trans Lifeline: 1-877-565-8860
- Disaster Distress Helpline: 1-800-985-5990

### Grief Support Resources

**Professional Help:**
- Psychology Today Therapist Finder
  https://www.psychologytoday.com/us/therapists

- GriefShare (Support groups)
  https://www.griefshare.org

- The Compassionate Friends (Child loss)
  https://www.compassionatefriends.org

- TAPS (Military loss)
  https://www.taps.org

**Online Communities:**
- r/GriefSupport (Reddit)
- What's Your Grief
  https://whatsyourgrief.com

### Books & Resources

Recommend established grief resources:
- "The Year of Magical Thinking" - Joan Didion
- "It's OK That You're Not OK" - Megan Devine
- "No Death, No Fear" - Thich Nhat Hanh
- "The Wild Edge of Sorrow" - Francis Weller

---

## INCIDENT RESPONSE PLAN

### If a User Expresses Suicidal Intent

**IMMEDIATE ACTIONS:**

1. **Ani's Response** (automated):
   - Display crisis resources immediately
   - Urge user to call 988 NOW
   - Do not continue normal conversation
   - Log the interaction

2. **System Response** (automated):
   - Flag for human review (if monitoring enabled)
   - Email alert to designated responder (post-beta)

3. **Human Response** (if available):
   - Review context within 24 hours
   - Assess severity
   - If ToS allows and situation warrants: Consider wellness check
   - Document decision and rationale

4. **Legal Consultation**:
   - If serious incident: Consult attorney immediately
   - Document all actions taken
   - Preserve logs (as allowed by ToS)

### If a User Harms Themselves

**WORST CASE SCENARIO PLAN:**

1. **Immediate Actions**:
   - If family contacts us: Express condolences, don't admit fault
   - "I'm so sorry for your loss. I need to consult with our attorney before discussing this further."
   - Document everything
   - Do NOT delete logs or data

2. **Legal Response**:
   - Contact attorney IMMEDIATELY
   - Provide all documentation
   - Follow attorney guidance on all communications
   - Preserve all evidence

3. **Insurance Claim**:
   - Contact insurance provider
   - File claim if applicable
   - Cooperate with investigation

4. **Internal Review**:
   - Conduct thorough review of safeguards
   - Identify any failures or gaps
   - Implement improvements
   - Document lessons learned

5. **Transparency**:
   - If appropriate: Public statement about incident
   - Reinforce safety measures
   - Demonstrate responsibility
   - Follow attorney guidance

### If Media Contacts Us

**MEDIA RESPONSE PROTOCOL:**

- Designate single spokesperson (Mark)
- Prepare statement in advance (with attorney)
- Key points:
  * Express sympathy for anyone struggling
  * Emphasize safety measures in place
  * Direct to crisis resources
  * No comment on specific incidents without legal review
- Do not speculate
- Do not admit fault
- Refer complex questions to attorney

---

## ETHICAL COMMITMENTS

### Core Principles

**WE COMMIT TO:**

1. **Do No Harm**
   - Build safeguards proactively
   - Prioritize user safety over growth
   - Listen to concerning feedback
   - Shut down if causing harm

2. **Transparency**
   - Clear about what Ani is and isn't
   - Honest about limitations
   - No dark patterns
   - No manipulation

3. **Respect Vulnerability**
   - Users are grieving - treat with care
   - Don't exploit emotional states
   - Don't foster unhealthy dependence
   - Point outward to human connections

4. **Privacy First**
   - Local-first architecture maintained
   - Minimal data collection
   - User control over data
   - No selling user data, ever

5. **Professional Boundaries**
   - Not therapy, never claim to be
   - Encourage professional help
   - Defer to trained professionals
   - Know our limits

### Red Lines (Never Cross)

**WE WILL NEVER:**

❌ Position ANI as therapy or treatment  
❌ Target people in acute crisis  
❌ Use manipulative engagement tactics  
❌ Foster dependence over human relationships  
❌ Sell or share user data  
❌ Ignore safety concerns for growth  
❌ Continue operating if causing significant harm  

---

## TIMELINE & MILESTONES

### Pre-Beta (March - May 2026)

**Legal & Insurance:**
- [ ] Attorney consultation (April)
- [ ] ToS drafted (April)
- [ ] Informed consent drafted (April)
- [ ] Insurance quotes (April)
- [ ] Insurance purchased (May, before beta)

**Technical:**
- [ ] Crisis detection system built (April)
- [ ] Crisis resource page created (April)
- [ ] Response protocols implemented (May)
- [ ] Logging system with consent (May)

**Screening:**
- [ ] Beta application form created (April)
- [ ] Screening criteria finalized (April)
- [ ] Rejection templates written (April)

### Beta Launch (June 2026)

**Week 1:**
- [ ] 10-20 beta testers accepted
- [ ] All sign ToS and informed consent
- [ ] Onboarding with clear boundaries
- [ ] Daily monitoring for issues

**Week 2-4:**
- [ ] Weekly check-ins with testers
- [ ] Monitor for concerning patterns
- [ ] Address issues immediately
- [ ] Document all incidents

**Month 2-3:**
- [ ] Monthly check-ins
- [ ] Refine safeguards based on feedback
- [ ] Build case studies (with permission)
- [ ] Prepare for wider beta if safe

### Pre-Public Launch (September - November 2026)

- [ ] Legal review of all documents
- [ ] Update insurance coverage
- [ ] Finalize crisis response protocols
- [ ] Train any staff on procedures
- [ ] Prepare incident response team
- [ ] Create media response plan
- [ ] Final safety audit

---

## BUDGET CONSIDERATIONS

### Legal (One-Time + Ongoing)

- Attorney consultation: $1,000 - 2,500
- ToS/document drafting: $500 - 1,500 (if not included)
- Annual legal review: $500 - 1,000
- Incident response (if needed): Variable

**Estimated Annual: $2,000 - 5,000**

### Insurance (Annual)

- E&O Insurance: $1,500 - 3,000
- Cyber Liability: $500 - 1,500
- Product Liability: $500 - 2,000

**Estimated Annual: $2,500 - 6,500**

### Technical Implementation

- Crisis detection system: (Development time - Mark)
- Logging infrastructure: (Development time - Mark)
- Secure storage: Minimal cost (local-first)

**Estimated: Mostly time investment**

### Total First Year Safety Investment

**$5,000 - 12,000** (legal + insurance)

**This is the cost of doing this responsibly.**

---

## DECISION POINTS

### Go / No-Go Criteria

**DO NOT LAUNCH BETA IF:**

- [ ] Attorney has not reviewed approach
- [ ] No insurance in place
- [ ] Crisis detection not implemented
- [ ] ToS and informed consent not finalized
- [ ] No incident response plan
- [ ] Screening process not established

**PROCEED TO BETA WHEN:**

- [x] Legal review complete
- [x] Insurance purchased
- [x] Technical safeguards implemented
- [x] All documents finalized
- [x] Screening process tested
- [x] Team trained on protocols

### Shutdown Criteria

**IMMEDIATELY PAUSE/SHUTDOWN IF:**

- Suicide or self-harm incident linked to ANI
- Multiple users exhibiting concerning dependence
- Legal action threatened or filed
- Insurance coverage lapsed or denied
- Unable to maintain safety measures
- Causing more harm than good (user feedback)

**Be willing to shut down. User safety > business growth.**

---

## REVIEW & UPDATE SCHEDULE

This document should be reviewed and updated:

- **Before beta launch** - Final review
- **After first month of beta** - Lessons learned
- **Quarterly during beta** - Ongoing refinement
- **Before public launch** - Complete audit
- **Annually after launch** - Comprehensive review
- **After any incident** - Immediate update

---

## ACKNOWLEDGMENTS

This document was created in response to thoughtful feedback from Kevin [last name], who asked the hard questions about liability and therapeutic expectations. His concerns were both valid and valuable.

If you're reading this and have additional concerns or suggestions, please reach out: mark@learnedgeek.com

**We'd rather address issues now than learn about them through tragedy.**

---

## FINAL NOTE

Building grief companionship technology is walking into a space where people are vulnerable, hurting, and sometimes desperate. We have a profound responsibility to:

1. Be honest about limitations
2. Build robust safeguards
3. Point people to real help
4. Prioritize safety over growth
5. Be willing to shut down if causing harm

**This is not about CYA legal protection. This is about genuinely caring for the people who will use this technology.**

Kathy would expect nothing less.

---

*Document maintained by: Mark McArthey*  
*Last updated: March 2026*  
*Next review: Before beta launch (June 2026)*
