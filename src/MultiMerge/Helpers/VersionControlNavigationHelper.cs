// Modified by Luca Persichini in 2026 for the MultiMerge fork; see NOTICE.txt.
using System;
using MultiMerge.VersionControl;
using Microsoft.TeamFoundation.Client;
using Microsoft.TeamFoundation.Common.Internal;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace MultiMerge
{
	public class VersionControlNavigationHelper
	{
		private static readonly Guid TfsProviderGuid = new Guid("4CA58AB2-18FA-4F8D-95D4-32DDF27D184C");
		private static readonly Guid GitProviderGuid = new Guid("11b8e6d7-c08b-4385-b321-321078cdd1f8");

		static VersionControlNavigationHelper()
		{
		}

        public static VersionControlProvider? GetActiveProvider(IServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (serviceProvider == null) return null;
            var service = serviceProvider.GetService<IVsRegisterScciProvider>() as IVsGetScciProviderInterface;
            if (service == null) return null;
            Guid id;
            try { service.GetSourceControlProviderID(out id); } catch (System.Runtime.InteropServices.COMException) { return null; }
            if (id == GitProviderGuid) return VersionControlProvider.Git;
            if (id == TfsProviderGuid) return VersionControlProvider.TeamFoundation;
            return null;
        }

        public static bool IsProviderActive(IServiceProvider serviceProvider, VersionControlProvider provider)
        {
            return GetActiveProvider(serviceProvider) == provider;
        }
		public static UIContext GetProviderUIContext(VersionControlProvider provider)
		{
			return UIContext.FromUIContextGuid(GetProviderGuid(provider));
		}

		public static bool IsConnectedToTfsCollectionAndProject(IServiceProvider provider)
		{
			var context = GetTeamFoundationContext(provider);
			if (context != null)
			{
				return context.HasCollection && context.HasTeamProject;
			}

			return false;
		}

		public static bool IsConnectedToTfsCollectionAndProject(ITeamFoundationContext context)
		{
			if (context != null)
			{
				return context.HasCollection && context.HasTeamProject;
			}

			return false;
		}

		private static Guid GetProviderGuid(VersionControlProvider provider)
		{
			switch (provider)
			{
				case VersionControlProvider.TeamFoundation:
					return TfsProviderGuid;
				case VersionControlProvider.Git:
					return GitProviderGuid;
				default:
					return Guid.Empty;
			}
		}

		public static ITeamFoundationContext GetTeamFoundationContext(IServiceProvider serviceProvider)
		{
			if (serviceProvider != null)
			{
				var tfContextManager = serviceProvider.GetService<ITeamFoundationContextManager>();
				if (tfContextManager != null)
				{
					var context = tfContextManager.CurrentContext;
					return context;
				}
			}

			return null;
		}

		public static string GetAuthorizedUser(IServiceProvider serviceProvider)
		{
			var context = GetTeamFoundationContext(serviceProvider);
			if (context != null && IsConnectedToTfsCollectionAndProject(context))
			{
				var vcs = context.TeamProjectCollection.GetService<VersionControlServer>();
				return vcs.AuthorizedUser;
			}

			return string.Empty;
		}
	}
}
