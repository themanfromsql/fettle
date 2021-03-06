﻿namespace Fettle.Core
{
    internal class NullEventListener : IEventListener
    {
        public void BeginCoverageAnalysisOfTestCase(string fullTestName, int index, int total) {}

        public void BeginMutationOfFile(string filePath, string baseSourceDirectory, int index, int total) {}
        public void MemberMutating(string name) {}
        public void SyntaxNodeMutating(int index, int total) {}
        public void MutantSurvived(SurvivingMutant survivingMutant) {}
        public void EndMutationOfFile(string filePath) {}
    }
}