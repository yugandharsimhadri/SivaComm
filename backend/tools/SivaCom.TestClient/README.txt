SivaComTest - test SivaCom messaging from the server
=======================================================

Full step-by-step guide, including sending your own approved template:
docs/TESTING.md in the SivaCom repository.

A small program that acts like HMS_WEB: it signs requests with an
application's key and drives the real API. Needs the .NET 10 runtime, which
the VM already has for SivaCom itself.

1. Unzip anywhere on the VM, e.g. C:\Deployments\sivacom\test.

2. Edit sivacom-test.json:
     BaseUrl   http://localhost:6073 on the VM, or
               https://sivacomapi.sivayaantechnologies.com to go through Cloudflare
     ClientId  KeyId  Secret
               copy from the hms-web entry under "Applications" in
               C:\Server\sivacomapi\appsettings.Production.json
     Phone     your WhatsApp number with country code, e.g. 919876543210 (optional)

3. Run from PowerShell in that folder (or double-click for a menu):

     .\SivaComTest.exe health           service up, database ready, key accepted
     .\SivaComTest.exe otp              sends an OTP on WhatsApp; type the code you get;
                                         it then verifies, consumes the grant and reports completion
     .\SivaComTest.exe welcome          sends the Welcome template and follows its status
     .\SivaComTest.exe status <id>      status of one message

   A phone number on the command line overrides Phone:  .\SivaComTest.exe otp 919876543210

What to expect
  - Until the WhatsApp settings (Meta) are filled in on the server, nothing reaches
    the phone: the server only logs the message, and the status stays SUBMITTED.
  - With Meta configured: SENT, then DELIVERED / READ once the webhook is set up.
  - The 3-OTPs-per-15-minutes limit (Otp settings) is per user; each run of this
    tool is a new test user, so repeated tests are not blocked by it.
  - Test messages appear in the dashboard under Messages, with references
    starting "test-".

The Secret in sivacom-test.json is a real signing key. Delete the file from the
VM when you have finished testing.
