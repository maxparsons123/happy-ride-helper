import { useState } from 'react';
import { X, MapPin, Navigation, User, Phone, Users, PoundSterling, Clock } from 'lucide-react';
import type { JobData, DriverPresence } from '@/hooks/use-driver-state';

interface DriverMenuProps {
  isOpen: boolean;
  onClose: () => void;
  allocatedJob: JobData | undefined;
  jobs: JobData[];
  presence: DriverPresence;
  onPresenceChange: (p: DriverPresence) => void;
  driverId: string;
  onJobStatusChange: (jobId: string, status: JobData['status']) => void;
}

type MenuView = 'currentJob' | 'jobHistory' | 'settings';

export function DriverMenu({ isOpen, onClose, allocatedJob, jobs, presence, onPresenceChange, driverId, onJobStatusChange }: DriverMenuProps) {
  const [view, setView] = useState<MenuView>('currentJob');

  if (!isOpen) return null;

  return (
    <div className="absolute bottom-0 left-0 right-0 h-[70vh] z-[2000] bg-white rounded-t-3xl shadow-[0_-4px_20px_rgba(0,0,0,0.2)] flex flex-col animate-slide-up overflow-hidden">
      {/* Nav Tabs */}
      <div className="flex bg-gradient-to-r from-[#1f6feb] to-[#1558b5] border-b-2 border-white/20 shrink-0">
        {([
          ['currentJob', '📍 Current Job'],
          ['jobHistory', '📋 History'],
          ['settings', '⚙️ Settings'],
        ] as const).map(([key, label]) => (
          <button
            key={key}
            onClick={() => setView(key)}
            className={`flex-1 py-4 text-sm font-bold transition-all ${
              view === key ? 'text-white bg-white/15' : 'text-white/70 hover:text-white hover:bg-black/10'
            }`}
          >
            {label}
          </button>
        ))}
        <button onClick={onClose} className="px-4 text-white/80 hover:text-white">
          <X size={20} />
        </button>
      </div>

      {/* Content */}
      <div className="flex-1 overflow-y-auto p-5 bg-[#f9fbfd]">
        {view === 'currentJob' && <CurrentJobView job={allocatedJob} onStatusChange={onJobStatusChange} />}
        {view === 'jobHistory' && <JobHistoryView jobs={jobs} />}
        {view === 'settings' && <SettingsView presence={presence} onPresenceChange={onPresenceChange} driverId={driverId} />}
      </div>
    </div>
  );
}

function CurrentJobView({ job, onStatusChange }: { job: JobData | undefined; onStatusChange: (jobId: string, status: JobData['status']) => void }) {
  if (!job) {
    return (
      <div className="text-center py-12 text-gray-400">
        <div className="text-6xl mb-4">🚕</div>
        <div className="text-xl font-extrabold mb-2 text-gray-600">No Active Job</div>
        <div className="text-sm leading-relaxed">
          Jobs you win will appear here.<br />
          Make sure you're set to "Available".
        </div>
      </div>
    );
  }

  const fareDisplay = job.fare ? `£${parseFloat(job.fare).toFixed(2)}` : 'To be agreed';

  return (
    <div className="bg-gradient-to-br from-green-50 to-green-100 rounded-2xl p-5 border-l-4 border-green-500 shadow-md space-y-4 relative">
      <span className="absolute top-3 right-3 text-3xl text-green-500/20 font-bold">✓</span>
      <JobDetail icon="🏷️" label="Job ID" value={job.jobId} />
      <JobDetail icon="📍" label="Pickup" value={job.pickupAddress} />
      <JobDetail icon="🏁" label="Drop-off" value={job.dropoff} highlight />
      <JobDetail icon="👤" label="Customer" value={job.customerName} />
      <JobDetail icon="📞" label="Phone" value={job.customerPhone} />
      {job.passengers && <JobDetail icon="👥" label="Passengers" value={job.passengers} />}
      <JobDetail icon="💷" label="Fare" value={fareDisplay} danger />
      <JobDetail icon="📝" label="Notes" value={job.notes} />
      <div className={`font-extrabold text-center py-2 rounded-xl uppercase tracking-wider text-sm ${
        job.status === 'arrived' ? 'bg-blue-200/50 text-blue-800' : 'bg-green-200/50 text-green-800'
      }`}>
        {job.status === 'arrived' ? 'ARRIVED AT PICKUP' : 'ALLOCATED'}
      </div>
      <div className="flex gap-3 pt-2">
        {job.status === 'allocated' && (
          <button
            onClick={() => onStatusChange(job.jobId, 'arrived')}
            className="flex-1 py-3 rounded-xl font-bold text-sm bg-blue-500 text-white shadow-md hover:bg-blue-600 transition-colors"
          >
            📍 Arrived at Pickup
          </button>
        )}
        {(job.status === 'allocated' || job.status === 'arrived') && (
          <button
            onClick={() => onStatusChange(job.jobId, 'completed')}
            className="flex-1 py-3 rounded-xl font-bold text-sm bg-green-500 text-white shadow-md hover:bg-green-600 transition-colors"
          >
            ✅ Complete Job
          </button>
        )}
      </div>
    </div>
  );
}

function JobHistoryView({ jobs }: { jobs: JobData[] }) {
  if (jobs.length === 0) {
    return (
      <div className="text-center py-12 text-gray-400">
        <div className="text-6xl mb-4 opacity-30">📋</div>
        <div className="text-xl font-bold mb-2 text-gray-600">No Jobs Yet</div>
        <div className="text-sm">Your job history will appear here.</div>
      </div>
    );
  }

  const statusStyles: Record<string, string> = {
    allocated: 'bg-green-100 text-green-800 border-l-green-500',
    arrived: 'bg-blue-100 text-blue-800 border-l-blue-500',
    completed: 'bg-gray-100 text-gray-600 border-l-gray-400',
    queued: 'bg-blue-50 text-blue-700 border-l-blue-400',
    rejected: 'bg-red-50 text-red-700 border-l-red-400',
    lost: 'bg-red-50 text-red-700 border-l-red-400',
  };

  return (
    <div className="space-y-4">
      {jobs.slice(0, 20).map((job) => {
        const time = new Date(job.timestamp);
        return (
          <div key={job.jobId + job.timestamp} className={`p-4 rounded-2xl border border-gray-200 bg-white shadow-sm border-l-4 ${statusStyles[job.status] || ''} hover:shadow-md transition-shadow`}>
            <div className="font-extrabold text-[#1f6feb] text-base mb-1">#{job.jobId}</div>
            <div className="text-sm text-gray-600 flex items-center gap-1.5 mb-1">📍 {job.pickupAddress}</div>
            <div className="text-sm text-[#1f6feb] font-semibold mb-1">⬇️ {job.dropoff}</div>
            <div className="text-sm text-gray-700 flex items-center gap-1.5">👤 {job.customerName}</div>
            {job.fare && (
              <div className="text-red-600 font-bold mt-1">💷 £{parseFloat(job.fare).toFixed(2)}</div>
            )}
            <div className="flex justify-between items-center mt-3 pt-3 border-t border-gray-100">
              <span className={`text-xs font-extrabold px-3 py-1 rounded-lg uppercase tracking-wide ${
                job.status === 'allocated' ? 'bg-green-100 text-green-800' :
                job.status === 'completed' ? 'bg-gray-200 text-gray-600' :
                job.status === 'rejected' || job.status === 'lost' ? 'bg-red-100 text-red-700' :
                'bg-blue-100 text-blue-700'
              }`}>
                {job.status}
              </span>
              <span className="text-xs text-gray-400">
                {time.toLocaleDateString()} {time.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
              </span>
            </div>
          </div>
        );
      })}
    </div>
  );
}

function SettingsView({ presence, onPresenceChange, driverId }: { 
  presence: DriverPresence; onPresenceChange: (p: DriverPresence) => void; driverId: string; 
}) {
  return (
    <div className="space-y-6">
      <div className="bg-white rounded-2xl p-5 shadow-sm border border-gray-200">
        <h3 className="font-bold text-gray-800 mb-4 text-lg">Driver Status</h3>
        <div className="flex gap-3">
          {(['available', 'busy', 'offline'] as const).map(p => (
            <button
              key={p}
              onClick={() => onPresenceChange(p)}
              className={`flex-1 py-3 rounded-xl font-bold text-sm capitalize transition-all ${
                presence === p 
                  ? p === 'available' ? 'bg-green-500 text-white shadow-md' 
                    : p === 'busy' ? 'bg-yellow-500 text-white shadow-md'
                    : 'bg-red-500 text-white shadow-md'
                  : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
              }`}
            >
              {p}
            </button>
          ))}
        </div>
      </div>

      <div className="bg-white rounded-2xl p-5 shadow-sm border border-gray-200">
        <h3 className="font-bold text-gray-800 mb-2 text-lg">Driver Info</h3>
        <div className="text-sm text-gray-500 font-mono bg-gray-50 p-3 rounded-lg">{driverId}</div>
      </div>
    </div>
  );
}

function JobDetail({ icon, label, value, highlight, danger }: {
  icon: string; label: string; value: string; highlight?: boolean; danger?: boolean;
}) {
  return (
    <div>
      <div className="font-bold text-green-900 text-sm mb-1 flex items-center gap-1.5">
        <span>{icon}</span> {label}
      </div>
      <div className={`text-base font-semibold px-1 ${
        highlight ? 'text-[#1f6feb] font-bold' : danger ? 'text-red-600 font-extrabold text-lg' : 'text-green-950'
      }`}>
        {value}
      </div>
    </div>
  );
}
