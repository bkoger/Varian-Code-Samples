////////////////////////////////////////////////////////////////////////////////
// StandAloneScript.cs
//
// Starting point for launching the automated planning script from the command line.
//  
// Applies to: ESAPI v15.6.
//
// Copyright (c) 2018 Varian Medical Systems, Inc.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy 
// of this software and associated documentation files (the "Software"), to deal 
// in the Software without restriction, including without limitation the rights 
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the Software is 
// furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in 
//  all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN 
// THE SOFTWARE.
////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Diagnostics;
using System.Linq;
using VMS.TPS.Common.Model.API;

[assembly: ESAPIScript(IsWriteable = true)]

namespace VMS.TPS
{

  class AutomatedPlanningDemo
  {
    // Change the patient ID here to run the script for a different patient.
    private const string PatientId = "RapidPlan-01";

    // The verification plans will be placed to a course with this Id.
    private const string VerificationCourseId = "QA";

    // The auto-generated plan plan will be placed to this course.
    private const string CourseId = "Demo";

    // Id for the demo plan.
    private const string PlanId = "Demo plan";

    // QA device for verification.
    private const string VerificationPatientId = "PH";//"MatrixxQA"; 
    private const string VerificationPatientStudyId = "7542";//"none";
    private const string VerificationPatientImageId = "Queasy(v2)155";//"CT MATRIXXEVO";

    // Default values that appear in the prescription dialog.
    public const int DefaultNumberOfFractions = 44;
    public const double DefaultDosePerFraction = 1.8;
    public const double DefaultMarginForPTVInMM = 5.0;
    

    [STAThread]
    static void Main(string[] args)
    {
      try
      {
        // use Windows authentication.
        using (var app = Common.Model.API.Application.CreateApplication())
        {
          Execute(app, PatientId, PlanId);
        }
      }
      catch (Exception e)
      {
        Console.Error.WriteLine(e.ToString());
      }

      Console.ReadLine();
    }

    private static void Execute(Common.Model.API.Application app, string patientId, string planId)
    {
      var patient = app.OpenPatientById(patientId);
      patient.BeginModifications();

      if (!Helpers.CheckStructures(patient))
      {
        return;
      }

      // The demo patient has structures in a set structure called "Prost30Oct2012".
      var structures = patient.StructureSets.Single(st => st.Id == "Prost30Oct2012");

      var course = Helpers.GetCourse(patient, CourseId);

      // If old courses exists, remove them. Remove also the structures generated by the script (in the previous run).
      Helpers.RemoveOldPlan(course, planId);
      Helpers.RemoveStructures(structures, new List<string> {"PTV", PlanGeneration.ExpandedCTVId, PlanGeneration.PTVSubOARSId});

      // Launch the prescription dialog.
      var prescriptionDialog = new PrescriptionDialog(patientId, DefaultDosePerFraction, DefaultNumberOfFractions, DefaultMarginForPTVInMM, structures.Structures)
      {
        Width = 300,
        Height = 350
      };
      double? dosePerFraction = null;
      int? numberOfFractions = null;
      double? marginForPTV = null;
      string ctvId = string.Empty;
      prescriptionDialog.Closed += (s, e) =>
      {
        dosePerFraction = prescriptionDialog.DosePerFraction;
        numberOfFractions = prescriptionDialog.NumberOfFractions;
        marginForPTV = prescriptionDialog.PTVMargin;
        ctvId = prescriptionDialog.SelectedStructure;
      };
      prescriptionDialog.ShowDialog();

      // If the user inputs are valid, proceed to plan creation.
      if (dosePerFraction.HasValue && numberOfFractions.HasValue && marginForPTV.HasValue && ctvId != string.Empty)
      {
        // Create new plan.
        var plan = course.AddExternalPlanSetup(structures);
        plan.Id = planId;

        // Re-direct output from trace to console.
        Trace.Listeners.Add(new ConsoleTraceListener());

        // Beam geometry generation.
        PlanGeneration.GenerateBeamGeometry(plan, dosePerFraction != null ? dosePerFraction.Value : 0,
                                                  numberOfFractions != null ? numberOfFractions.Value : 0,
                                                  marginForPTV != null ? marginForPTV.Value : 0,
                                                  ctvId);

        // DVH estimation.
        var structureMatches = PlanGeneration.GetStructureMatches(plan);
        structureMatches.Remove(PlanGeneration.ExpandedCTVId);
        structureMatches.Add(PlanGeneration.PTVSubOARSId, new ModelStructure("PTV", ModelStructureType.Target));
        PlanGeneration.CalculateDVHEstimates(plan, structureMatches);  // can't locate TPS Core

        // Add normal tissue objectives.
        PlanGeneration.AddNTO(plan);

        // Save beam geometry and DVH estimation results.
        var message = "Beam geometry generation and DVH estimation completed.";
        SaveAndShowMessage(app, message);

        // Optimization.
        PlanGeneration.Optimize(plan);

        // Save optimization results.
        message = "Optimization completed.";
        SaveAndShowMessage(app, message);
		
		// Run Multicriteria Optimization (MCO)
        PlanGeneration.RunMCO(patient, course.Id, plan.Id);

        // Save MCO results.
        message = "Multicriteria Optimization completed.";
        SaveAndShowMessage(app, message);
        
        // Caluclate dose.
        PlanGeneration.CalculateDose(plan);
        Trace.WriteLine("\nPlan successfully generated.\n");

        // Normalize plan.
        PlanGeneration.Normalize(plan, structureMatches);
        app.SaveModifications();

        // Report DVHs.
        var outputPath = @"C:\Temp\dvh_mco.svg";
        const string browser = @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe";
        var structuresForReporting = plan.StructureSet.Structures.Where(st => structureMatches.ContainsKey(st.Id));
        const int dvhWidth = 512;
        const int dvhHeight = 256;
        SVGFromDVH.SaveSVGFromStructures(outputPath, plan, structuresForReporting, dvhWidth, dvhHeight);

        outputPath = @"C:\Temp";
        var reportLocation = PlanQualityReporter.CreateReport(plan, structureMatches, outputPath);

        Trace.WriteLine("\nReport successfully generated.\n");

        // Display the generated report in web browser.
        Process.Start(/*browser,*/ reportLocation);
        Thread.Sleep(1000);

        // Ask user if we want to proceed to plan verification.
        const string title = "Quality assurance";
        message = "Proceed to creation of verification plan?";
        var res = MessageBox.Show(new Window{ Topmost = true}, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
          // Create verification plan.
          Trace.Write("\nRetrieving CT image of the QA device...\n");
          var qaStructures = patient.StructureSets.Where(set => set.Id == VerificationPatientImageId).ToList();

          // If we have already loaded the structures for verification, do not load them again. Currently, ESAPI doesn't offer methods to remove them... (has to be done by hand in Eclipse).
          var verificationStructures = qaStructures.Any() ? qaStructures.First() : plan.Course.Patient.CopyImageFromOtherPatient(VerificationPatientId, VerificationPatientStudyId, VerificationPatientImageId);
          CreateVerificationPlan(app, plan, verificationStructures);
          app.SaveModifications();

          Trace.WriteLine("\nVerification plans succesfully created.\n");  
        }
       
        Trace.WriteLine("\nScript successfully executed.\n");
      }
      else
      {
        const string message = "Please provide a valid prescription.";
        const string title = "Invalid prescription";
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
      }

    }

    private static void CreateVerificationPlan(Common.Model.API.Application app, ExternalPlanSetup verifiedPlan, StructureSet verificationStructures)
    {
      var course = Helpers.GetCourse(verifiedPlan.Course.Patient, VerificationCourseId);

      Trace.WriteLine("\nCreating verification plans...\n");

      // Create an individual verification plan for each field.
      foreach (var beam in verifiedPlan.Beams)
      {
        PlanGeneration.CreateVerificationPlan(course, new List<Beam> { beam }, verifiedPlan, verificationStructures, beam.Id, calculateDose: false);
      }

      // Create a verification plan that contains all fields.
      PlanGeneration.CreateVerificationPlan(course, verifiedPlan.Beams, verifiedPlan, verificationStructures, "All fields", calculateDose: true);
    }

    private static void SaveAndShowMessage(Common.Model.API.Application app, string message)
    {
      app.SaveModifications();

      // Make sure that the message is on top of all other windows.
      MessageBox.Show(new Window{ Topmost = true}, message); 
    }

  }
}

