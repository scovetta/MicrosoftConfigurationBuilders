// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See the License.txt file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading;

namespace Microsoft.Configuration.ConfigurationBuilders
{
    /// <summary>
    /// Disposable object to help detect and handle problems with recursion.
    /// </summary>
    internal class RecursionCheck : IDisposable
    {
        private static AsyncLocal<Stack<string>> sectionsEntered = new AsyncLocal<Stack<string>>();

        internal bool ShouldStop = false;
        private string sectionName = null;
        private string builderId;

        internal RecursionCheck(string builderId, string sectionName, RecursionBehavior behavior)
        {
            // If behavior says "do nothing"... then let's do nothing.
            if (behavior == RecursionBehavior.None)
                return;

            sectionsEntered.Value = sectionsEntered.Value ?? new Stack<string>();
            this.builderId = builderId;

            // Have we been in this section before?
            if (sectionsEntered.Value.Contains(sectionName))
            {
                // Should we throw an exception?
                if (behavior == RecursionBehavior.Throw)
                {
                    // Don't touch sectionsEntered. We did not add to it. We should not take from it or
                    // throw it away in case somebody wants to handle this exception.
                    throw new InvalidOperationException($"The ConfigurationBuilder {builderId} has detected recursive processing of the '{sectionName}' section.");
                }

                // If we don't throw, should we at least stop going down the rabbit hole?
                // Don't bother tracking this 'sectionName' since we'll immediately start unwinding.
                ShouldStop = (behavior == RecursionBehavior.Stop);  // This is always true?
                return;
            }

            // If we get here, then we will allow the section to be processed. Lets add the section name
            // to the list of entered sections, and also remember it so we can remove it from the list when
            // we are disposed.
            this.sectionName = sectionName;
            sectionsEntered.Value.Push(sectionName);
        }

        public void Dispose()
        {
            // If we tracked the entering of a section... stop tracking now.
            if (sectionName != null)
            {
                string poppedName = sectionsEntered.Value.Pop();

                // Sanity check to make sure we are un-tracking what we expect to un-track.
                if (poppedName != sectionName) {
                    throw new InvalidOperationException($"The ConfigurationBuilder {builderId} has detected a mix up while processing of the '{sectionName}' and '{poppedName}' sections.");
                }
            }
        }
    }
}
