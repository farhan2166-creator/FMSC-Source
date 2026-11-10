namespace FMSC_SMS.Core;
/// <summary>Authoritative text lengths for every schema-backed text field.</summary>
public static class SchemaLengths
{
    // Students (the surface)
    public const int StudentName = 500;                              // Students.StudentName
    public const int StudentFatherName = 500;                        // Students.FatherName
    public const int RegistrationNumber = 40;                        // Students.RegistrationNumber
    public const int StudentGuardianName = 500;                      // Students.GuardianName
    public const int StudentGuardianRelation = 50;                   // Students.GuardianRelation
    public const int BFormNumber = 50;                               // Students.BFormNumber
    public const int StudentContactNumber = 30;                      // Students.ContactNumber
    public const int BiseEnrolmentNumber = 50;                       // Students.BiseEnrolmentNumber
    public const int StudentPreviousSchool = 500;                    // Students.PreviousSchool
    public const int StudentFatherOccupation = 250;                  // Students.FatherOccupation
    public const int StudentCasteTribe = 100;                        // Students.CasteTribe
    public const int StudentPlaceOfBirth = 500;                      // Students.PlaceOfBirth
    // Shared text columns that are the SAME length in every table.
    public const int City = 100;                                     // Campuses/SchoolProfile.City
    public const int ContactNumber = 30;                             // Students/Staff/Campuses
    public const int SchoolProfileContactNumber = 150;               // SchoolProfile.ContactNumber (comma-separated list)
    public const int AlternateContactNumber = 30;                    // Students/Staff
    public const int Address = 500;                                  // Students/Staff.Address
    public const int AddressLine = 500;                              // Campuses/SchoolProfile.AddressLine
    public const int Email = 150;                                    // Campuses/SchoolProfile.Email
    public const int Gender = 20;                                    // Students/Staff/certificate tables
    public const int Status = 30;                                    // Students/Enrollments.Status
    public const int ListingRemarks = 500;                           // Remarks on ordinary operational tables
    // Users and roles
    public const int Username = 100;                                 // Users.Username
    public const int RoleName = 50;                                  // Roles.RoleName
    // Campus
    public const int CampusCode = 30;                                // Campuses.CampusCode
    public const int CampusName = 150;                               // Campuses.CampusName
    public const int CampusType = 30;                                // Campuses.CampusType
    // Academic calendar
    public const int YearName = 20;                                  // AcademicYears.YearName
    public const int AcademicYearRemarks = 500;                      // AcademicYears.Remarks
    // Classes, sections, subjects
    public const int ClassCode = 30;                                 // Classes.ClassCode
    public const int ClassName = 100;                                // Classes.ClassName
    public const int SectionCode = 30;                               // Sections.SectionCode
    public const int SectionName = 50;                               // Sections.SectionName
    public const int SectionDescription = 250;                       // Sections.Description
    public const int SubjectCode = 30;                               // Subjects.SubjectCode
    public const int SubjectName = 100;                              // Subjects.SubjectName
    public const int SubjectDescription = 250;                       // Subjects.Description
    // Fees
    public const int FeeTypeCode = 30;                               // FeeTypes.FeeTypeCode
    public const int FeeTypeName = 100;                              // FeeTypes.FeeTypeName
    public const int FeeTypeDescription = 250;                       // FeeTypes.Description
    // Staff (note the FatherName divergence from Students)
    public const int StaffNumber = 30;                               // Staff.StaffNumber
    public const int StaffName = 150;                                // Staff.StaffName
    public const int StaffFatherName = 150;                          // Staff.FatherName
    public const int Cnic = 25;                                      // Staff.CNIC
    public const int Qualification = 250;                            // Staff.Qualification
    public const int Specialization = 250;                           // Staff.Specialization
    public const int Designation = 100;                              // StaffEmployment/SchoolOfficials.Designation
    public const int Department = 100;                               // StaffEmployment.Department
    public const int EmploymentType = 50;                            // StaffEmployment.EmploymentType
}
