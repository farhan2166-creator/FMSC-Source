using FMSC_SMS.Core;

int checks = 0;
void Check(bool result, string name)
{
    if (!result) throw new InvalidOperationException(name);
    checks++;
}
foreach (string? value in new string?[] { null, "", " ", "03001234567", "0300-1234567", "+92 300 1234567", "042-31234567", "03001234567,03121234567", "0300-1234567, 042-31234567", "03001234567, 03121234567, 03331234567, 03441234567" })
    Check(SchoolContactValidation.IsValidPhoneList(value), $"Valid phone list: {value}");
foreach (string value in new[] { "123", "abc", "03001234567x", "03001234567;03121234567", ",03001234567", "03001234567,", "03001234567,,03121234567", "03001234567, ", "03001234567,123", "03001234567\n", "03001234567\t", "0300/1234567", "++923001234567" })
    Check(!SchoolContactValidation.IsValidPhoneList(value), $"Invalid phone list: {value}");
foreach (char value in "0123456789+- (),")
    Check(SchoolContactValidation.IsPhoneCharacter(value), $"Allowed phone character: {value}");
foreach (char value in "abc@;/\t\n")
    Check(!SchoolContactValidation.IsPhoneCharacter(value), $"Forbidden phone character: {value}");
foreach (string? value in new string?[] { null, "", " ", "fmscsarband@gmail.com", "school.office+fees@example.edu.pk" })
    Check(EmailAddressValidator.IsBlankOrValid(value), $"Valid optional email: {value}");
foreach (string value in new[] { "abc", "abc@", "@gmail.com", "a@@gmail.com", "a b@gmail.com", "a@gmail", "a@gmail..com", "a..b@gmail.com", "Name <a@gmail.com>", "a@gmail.com,b@gmail.com" })
    Check(!EmailAddressValidator.IsBlankOrValid(value), $"Malformed email: {value}");
Console.WriteLine($"PASS: {checks} contact-validation checks. Email checks cover syntax, not domain or mailbox existence.");
