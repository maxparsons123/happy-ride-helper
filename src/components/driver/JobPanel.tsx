import { useState, useEffect, useCallback } from 'react';
import { Check, X, Clock, MapPin, Navigation, User, Phone, Users, PoundSterling, FileText } from 'lucide-react';
import type { JobData } from '@/hooks/use-driver-state';

interface JobPanelProps {
  job: JobData;
  onAccept: (job: JobData) => void;
  onReject: (job: JobData) => void;
}

export function JobPanel({ job, onAccept, onReject }: JobPanelProps) {
  const [secondsLeft, setSecondsLeft] = useState(job.biddingWindowSec || 30);
  const isCritical = secondsLeft <= 10;

  useEffect(() => {
    setSecondsLeft(job.biddingWindowSec || 30);
    const interval = setInterval(() => {
      setSecondsLeft(prev => {
        if (prev <= 1) { clearInterval(interval); onReject(job); return 0; }
        return prev - 1;
      });
    }, 1000);
    return () => clearInterval(interval);
  }, [job.jobId]);

  const fareDisplay = job.fare ? `£${parseFloat(job.fare).toFixed(2)}` : 'To be agreed';

  return (
    <div className="absolute bottom-0 left-0 right-0 z-[1000] bg-white rounded-t-3xl shadow-[0_-4px_20px_rgba(0,0,0,0.15)] animate-slide-up max-h-[85vh] overflow-y-auto">
      <div className="p-5">
        {/* Header */}
        <h2 className="text-center text-xl font-extrabold text-[#1f6feb] pb-3 mb-4 border-b-2 border-gray-100 relative">
          🚕 New Job Request
          <span className="absolute bottom-[-2px] left-1/2 -translate-x-1/2 w-12 h-[3px] bg-[#1f6feb] rounded-full" />
        </h2>

        {/* Job Info Grid */}
        <div className="bg-gradient-to-br from-gray-50 to-gray-100 rounded-2xl p-4 border border-gray-200 space-y-3">
          <InfoRow icon={<FileText size={16} />} label="Job ID" value={job.jobId} />
          <InfoRow icon={<MapPin size={16} />} label="Pickup" value={job.pickupAddress} />
          <InfoRow icon={<Navigation size={16} />} label="Drop-off" value={job.dropoff} highlight />
          <InfoRow icon={<User size={16} />} label="Customer" value={job.customerName} />
          <InfoRow icon={<Phone size={16} />} label="Phone" value={job.customerPhone} />

          {job.passengers && (
            <div className="bg-gradient-to-r from-blue-50 to-blue-100 border-l-4 border-blue-300 p-3 rounded-xl flex items-center gap-3 shadow-sm">
              <div className="w-9 h-9 bg-white rounded-full flex items-center justify-center shadow-sm">
                <Users size={18} className="text-blue-700" />
              </div>
              <span className="font-bold text-blue-900 text-base">{job.passengers}</span>
            </div>
          )}

          <InfoRow icon={<PoundSterling size={16} />} label="Fare" value={fareDisplay} danger />
          
          <div className="bg-blue-50 p-2.5 rounded-lg border-l-3 border-[#1f6feb] italic text-gray-600 text-sm">
            <span className="font-bold text-gray-700 not-italic">📝 Notes: </span>{job.notes}
          </div>
        </div>

        {/* Bidding Timer */}
        <div className={`mt-3 text-center py-3 rounded-xl font-extrabold text-sm border ${
          isCritical 
            ? 'bg-red-50 border-red-200 text-red-600' 
            : 'bg-blue-50 border-blue-200 text-[#1f6feb]'
        }`}>
          ⏳ Bidding closes in:{' '}
          <span className={`text-2xl font-black inline-block min-w-[40px] ${
            isCritical ? 'animate-pulse text-red-600' : ''
          }`}>
            {secondsLeft}
          </span>{' '}
          seconds
        </div>

        {/* Action Buttons */}
        <div className="flex gap-3 mt-4 pt-4 border-t-2 border-gray-100">
          <button
            onClick={() => onAccept(job)}
            className="flex-1 py-4 rounded-2xl bg-gradient-to-br from-green-500 to-green-700 text-white font-extrabold text-lg flex flex-col items-center gap-1 shadow-[0_4px_12px_rgba(34,197,94,0.35)] hover:shadow-[0_6px_16px_rgba(34,197,94,0.5)] active:translate-y-[2px] transition-all"
          >
            <Check size={24} />
            ACCEPT JOB
          </button>
          <button
            onClick={() => onReject(job)}
            className="flex-1 py-4 rounded-2xl bg-gradient-to-br from-red-500 to-red-700 text-white font-extrabold text-lg flex flex-col items-center gap-1 shadow-[0_4px_12px_rgba(220,53,69,0.35)] hover:shadow-[0_6px_16px_rgba(220,53,69,0.5)] active:translate-y-[2px] transition-all"
          >
            <X size={24} />
            REJECT
          </button>
        </div>
      </div>
    </div>
  );
}

function InfoRow({ icon, label, value, highlight, danger }: { 
  icon: React.ReactNode; label: string; value: string; highlight?: boolean; danger?: boolean 
}) {
  return (
    <div>
      <div className="flex items-center gap-1.5 text-gray-600 text-xs font-bold mb-1">
        {icon} {label}
      </div>
      <div className={`text-base font-medium break-words ${
        highlight ? 'text-[#1f6feb] font-bold text-[17px]' :
        danger ? 'text-red-600 font-extrabold text-lg' :
        'text-gray-900'
      }`}>
        {value}
      </div>
    </div>
  );
}
