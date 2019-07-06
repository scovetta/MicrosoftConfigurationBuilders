// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See the License.txt file in the project root for full license information.

namespace Microsoft.Configuration.ConfigurationBuilders
{
    /// <summary>
    /// Possible behaviors when dealing with config builder recursion.
    /// </summary>
    public enum RecursionBehavior
    {
        /// <summary>
        /// [Default] Throw when reprocessing the same section.
        /// </summary>
        Throw,
        /// <summary>
        /// [!!May yield unexpected results in recursive conditions!!] Stop recursing, return without processing.
        /// </summary>
        Stop,
        /// <summary>
        /// [!!May result in deadlocks or stack overflow in recursive conditions!!] Do nothing. Continue recursing.
        /// </summary>
        None
    }
}
