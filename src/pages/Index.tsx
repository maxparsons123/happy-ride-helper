import { TaxiChatbot } from "@/components/TaxiChatbot";
import { Link } from "react-router-dom";

const Index = () => {
  return (
    <div className="relative">
      <Link
        to="/driver"
        className="fixed top-4 right-4 z-50 bg-gradient-to-r from-[#FFD700] to-[#FFC107] text-black font-extrabold px-5 py-3 rounded-full shadow-lg hover:shadow-xl hover:scale-105 transition-all flex items-center gap-2 text-sm"
      >
        🚕 Driver App
      </Link>
      <TaxiChatbot />
    </div>
  );
};

export default Index;
